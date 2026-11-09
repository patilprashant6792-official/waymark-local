using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Tests.Support;

/// <summary>A workspace store that keeps everything in a list. Names are compared ignoring case, like the real store.</summary>
internal sealed class InMemoryWorkspaceStore : IWorkspaceStore
{
    private readonly List<Workspace> _items = [];
    private long _nextId;

    public InMemoryWorkspaceStore(params Workspace[] items)
    {
        _items.AddRange(items);
        _nextId = items.Length == 0 ? 1 : items.Max(item => item.Id) + 1;
    }

    public Task<Workspace?> AddAsync(string name, string path, DateTimeOffset now, CancellationToken ct)
    {
        var taken = _items.Any(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (taken)
        {
            return Task.FromResult<Workspace?>(null);
        }

        var added = new Workspace(_nextId++, name, path, true, now, null);
        _items.Add(added);
        return Task.FromResult<Workspace?>(added);
    }

    public Task<Workspace?> GetByNameAsync(string name, CancellationToken ct) =>
        Task.FromResult(_items.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Workspace>>([.. _items]);

    public Task<bool> SetEnabledAsync(long id, bool enabled, CancellationToken ct) =>
        Task.FromResult(Replace(id, item => item with { Enabled = enabled }));

    public Task<bool> MarkIndexedAsync(long id, DateTimeOffset at, CancellationToken ct) =>
        Task.FromResult(Replace(id, item => item with { LastIndexedUtc = at }));

    public Task<bool> RemoveAsync(long id, CancellationToken ct) =>
        Task.FromResult(_items.RemoveAll(item => item.Id == id) > 0);

    private bool Replace(long id, Func<Workspace, Workspace> change)
    {
        var index = _items.FindIndex(item => item.Id == id);
        if (index < 0)
        {
            return false;
        }

        _items[index] = change(_items[index]);
        return true;
    }
}

/// <summary>An access rule store in memory that counts reads, so a test can prove the cache saves them.</summary>
internal sealed class InMemoryAccessRuleStore : IAccessRuleStore
{
    private readonly List<AccessRule> _rules = [];
    private long _nextId = 1;

    public int ListCalls { get; private set; }

    /// <summary>Runs inside every list call before it returns; a test uses it to change things while the rules are being read.</summary>
    public Func<long, Task>? OnList { get; set; }

    /// <summary>When set, every list call throws it.</summary>
    public Exception? FailWith { get; set; }

    public AccessRule Add(long workspaceId, AccessRuleKind kind, string pattern, bool enabled = true)
    {
        var rule = new AccessRule(_nextId++, workspaceId, kind, pattern, enabled, string.Empty, DateTimeOffset.UnixEpoch);
        _rules.Add(rule);
        return rule;
    }

    public async Task<IReadOnlyList<AccessRule>> ListAsync(long workspaceId, CancellationToken ct)
    {
        ListCalls++;
        if (OnList is not null)
        {
            await OnList(workspaceId);
        }

        if (FailWith is not null)
        {
            throw FailWith;
        }

        return [.. _rules.Where(rule => rule.WorkspaceId == workspaceId)];
    }

    public Task<AccessRule?> GetAsync(long id, CancellationToken ct) =>
        Task.FromResult(_rules.FirstOrDefault(rule => rule.Id == id));

    public Task<AccessRule?> AddAsync(long workspaceId, AccessRuleKind kind, string pattern, string note, DateTimeOffset now, CancellationToken ct)
    {
        if (_rules.Any(rule => rule.WorkspaceId == workspaceId && rule.Kind == kind && string.Equals(rule.Pattern, pattern, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult<AccessRule?>(null);
        }

        var rule = new AccessRule(_nextId++, workspaceId, kind, pattern, true, note, now);
        _rules.Add(rule);
        return Task.FromResult<AccessRule?>(rule);
    }

    public Task<bool> SetEnabledAsync(long id, bool enabled, CancellationToken ct)
    {
        var index = _rules.FindIndex(rule => rule.Id == id);
        if (index < 0)
        {
            return Task.FromResult(false);
        }

        _rules[index] = _rules[index] with { Enabled = enabled };
        return Task.FromResult(true);
    }

    public Task<bool> RemoveAsync(long id, CancellationToken ct) =>
        Task.FromResult(_rules.RemoveAll(rule => rule.Id == id) > 0);
}
