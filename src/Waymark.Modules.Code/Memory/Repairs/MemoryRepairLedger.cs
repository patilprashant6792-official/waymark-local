using System.Collections.Concurrent;
using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory.Repairs;

/// <summary>
/// What each agent did lately, per client and workspace, as far as repairs need it: the files it read and the repairs it made. In memory only; an entry with no
/// activity for <see cref="IdleTime"/> is a new session the next time. Paths are compared without case and without a member or line range.
/// </summary>
internal sealed class MemoryRepairLedger(TimeProvider time) : IMemoryRepairLedger
{
    internal static readonly TimeSpan IdleTime = TimeSpan.FromHours(3);

    private const int MaxPathsPerSession = 2_000;
    private const int MaxSessions = 500;

    private readonly ConcurrentDictionary<(string Client, string Workspace), Session> _sessions = new();

    public void RecordRead(string client, string workspace, string path)
    {
        var file = FileOf(path);
        if (file.Length == 0)
        {
            return;
        }

        var session = Touch(client, workspace);
        lock (session)
        {
            if (session.Paths.Count < MaxPathsPerSession)
            {
                session.Paths.Add(file);
            }
        }
    }

    public bool HasRead(string client, string workspace, string path)
    {
        var file = FileOf(path);
        if (file.Length == 0 || !_sessions.TryGetValue(Key(client, workspace), out var session) || IsIdle(session))
        {
            return false;
        }

        lock (session)
        {
            return session.Paths.Contains(file);
        }
    }

    public void RecordRepair(string client, string workspace)
    {
        var session = Touch(client, workspace);
        Interlocked.Increment(ref session.Repairs);
    }

    public int RepairsDone(string client, string workspace) =>
        _sessions.TryGetValue(Key(client, workspace), out var session) && !IsIdle(session) ? Volatile.Read(ref session.Repairs) : 0;

    public void ForgetReads(string workspace, IReadOnlyCollection<string>? paths)
    {
        var name = (workspace ?? string.Empty).Trim().ToLowerInvariant();
        var files = paths?.Select(FileOf).Where(file => file.Length > 0).ToList();
        foreach (var (key, session) in _sessions)
        {
            if (!string.Equals(key.Workspace, name, StringComparison.Ordinal))
            {
                continue;
            }

            lock (session)
            {
                if (files is null)
                {
                    session.Paths.Clear();
                }
                else
                {
                    foreach (var file in files)
                    {
                        session.Paths.Remove(file);
                    }
                }
            }
        }
    }
    /// <summary>The file part of a step or anchor text ('src/A.cs:Type.Member' and 'src/A.cs#L10-20' are src/A.cs), with '/' separators.</summary>
    internal static string FileOf(string? path)
    {
        var text = (path ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
        while (text.StartsWith("./", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        var cut = text.IndexOfAny([':', '#']);
        return (cut > 0 ? text[..cut] : text).Trim();
    }

    private Session Touch(string client, string workspace)
    {
        var now = time.GetUtcNow();
        var key = Key(client, workspace);
        var session = _sessions.AddOrUpdate(
            key,
            _ => new Session(now),
            (_, old) => now - old.LastUtc > IdleTime ? new Session(now) : old);
        session.LastUtc = now;
        if (_sessions.Count > MaxSessions)
        {
            Prune(now);
        }

        return session;
    }

    private bool IsIdle(Session session) => time.GetUtcNow() - session.LastUtc > IdleTime;

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, session) in _sessions)
        {
            if (now - session.LastUtc > IdleTime)
            {
                _sessions.TryRemove(key, out _);
            }
        }
    }

    private static (string, string) Key(string client, string workspace) =>
        ((client ?? string.Empty).Trim().ToLowerInvariant(), (workspace ?? string.Empty).Trim().ToLowerInvariant());

    private sealed class Session(DateTimeOffset started)
    {
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public DateTimeOffset LastUtc { get; set; } = started;

        public int Repairs;
    }
}
