using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Waymark.Modules.Commands.Execution;

/// <summary>
/// The processes below a program: a dev server started through npm is npm, then node, then often more. Waymark needs them to stop a program
/// cleanly (every process gets the signal), to tell which listening ports belong to a terminal, and to end children that were left behind.
/// On macOS and Linux it reads the system's process table with ps; on Windows the job object of the run already owns the tree, so the list is empty.
/// A process is identified by its id and its start time together, so an id the system gave to another program later is never touched.
/// </summary>
public sealed class ProcessTree(ProcessRunner runner, ExecutableLocator executables)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SameStart = TimeSpan.FromSeconds(2);

    /// <summary>The ids of every process below <paramref name="root"/>, nearest first. Empty on Windows or when the table cannot be read.</summary>
    public async Task<IReadOnlyList<int>> DescendantsAsync(int root, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows() || root <= 0)
        {
            return [];
        }

        var ps = executables.Find("ps") ?? (File.Exists("/bin/ps") ? "/bin/ps" : null);
        if (ps is null)
        {
            return [];
        }

        var (code, output) = await runner.CaptureAsync(ps, ["-A", "-o", "pid=,ppid="], Timeout, ct).ConfigureAwait(false);
        if (code != 0)
        {
            return [];
        }

        var children = new Dictionary<int, List<int>>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent))
            {
                continue;
            }

            if (!children.TryGetValue(parent, out var list))
            {
                list = [];
                children[parent] = list;
            }

            list.Add(pid);
        }

        var result = new List<int>();
        var seen = new HashSet<int> { root };
        var pending = new Queue<int>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var next = pending.Dequeue();
            if (!children.TryGetValue(next, out var list))
            {
                continue;
            }

            foreach (var child in list.Where(seen.Add))
            {
                result.Add(child);
                pending.Enqueue(child);
            }
        }

        return result;
    }

    /// <summary>When the process started, in UTC; null when it is gone or cannot be read.</summary>
    public static DateTimeOffset? StartTimeOf(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="pid"/> still is the process that started at <paramref name="started"/>.</summary>
    public static bool IsSameProcess(int pid, DateTimeOffset? started) =>
        started is { } expected && StartTimeOf(pid) is { } actual && (actual - expected).Duration() <= SameStart;

    /// <summary>Ends a process and everything below it. Quiet when it is already gone.</summary>
    public static void KillTree(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone, or not ours to end.
        }
    }
}
