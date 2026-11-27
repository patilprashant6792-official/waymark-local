using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace Waymark.Modules.Commands.Execution;

/// <summary>
/// A program that keeps running in a terminal: its input stays open so lines can be typed to it, it has no time limit unless one is given, and it is
/// stopped the way a developer stops it (Ctrl+C, then a request to terminate, then a kill of the whole tree). <see cref="Exited"/> completes once the
/// program ended and everything it printed was read; children it left behind are ended then too.
/// </summary>
public sealed class RunningProcess : IDisposable
{
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TerminateWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);

    private readonly Process _process;
    private readonly Task _readers;
    private readonly CancellationTokenSource _pumps;
    private readonly SemaphoreSlim _input = new(1, 1);
    private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<int, DateTimeOffset?> _descendants = new();
    private ProcessJob? _job;
    private int _stopping;

    internal RunningProcess(Process process, ProcessJob? job, Task readers, CancellationTokenSource pumps, string programName)
    {
        _process = process;
        _job = job;
        _readers = readers;
        _pumps = pumps;
        ProgramName = programName;
        Pid = process.Id;
        StartedUtc = ProcessTree.StartTimeOf(process.Id) ?? DateTimeOffset.UtcNow;
    }

    public int Pid { get; }

    public DateTimeOffset StartedUtc { get; }

    public string ProgramName { get; }

    /// <summary>The exit code once the program ended and its output was read (null when it reported none).</summary>
    public Task<int?> Exited => _exited.Task;

    public bool HasExited => _exited.Task.IsCompleted;

    /// <summary>True once a stop was asked for.</summary>
    public bool IsStopping => Volatile.Read(ref _stopping) == 1;

    /// <summary>True when the program was ended because its maximum lifetime passed.</summary>
    public bool LifetimeEnded { get; private set; }

    /// <summary>The ids of the processes below the program, as last seen.</summary>
    public IReadOnlyCollection<int> KnownDescendants => _descendants.Keys.ToList();

    internal void Begin(TimeSpan? maxLifetime) => _ = MonitorAsync(maxLifetime);

    /// <summary>Remembers processes found below the program, so they can be ended if they outlive it.</summary>
    public void Remember(IEnumerable<int> pids)
    {
        ArgumentNullException.ThrowIfNull(pids);
        foreach (var pid in pids)
        {
            if (!_descendants.ContainsKey(pid))
            {
                _descendants[pid] = ProcessTree.StartTimeOf(pid);
            }
        }
    }

    /// <summary>Writes text to the program's input. False when the program ended or its input is closed.</summary>
    public async Task<bool> WriteAsync(string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (HasExited)
        {
            return false;
        }

        await _input.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteAsync(text.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            _input.Release();
        }
    }

    /// <summary>
    /// Stops the program as a developer would: Ctrl+C (SIGINT) to it and every process below it, then a request to terminate (SIGTERM), then a kill
    /// of the whole tree. On Windows, where a program without a console cannot get Ctrl+C, the tree is ended at once. Returns once it ended.
    /// </summary>
    public async Task StopAsync(ProcessTree tree, TimeSpan grace)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (Interlocked.Exchange(ref _stopping, 1) == 1 || HasExited)
        {
            await WaitAsync(KillWait + grace).ConfigureAwait(false);
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            var below = await tree.DescendantsAsync(Pid, CancellationToken.None).ConfigureAwait(false);
            Remember(below);
            Signal(UnixSignals.Interrupt, below);
            if (await WaitAsync(grace).ConfigureAwait(false))
            {
                return;
            }

            Signal(UnixSignals.Terminate, below);
            if (await WaitAsync(TerminateWait).ConfigureAwait(false))
            {
                return;
            }
        }

        Kill();
        await WaitAsync(KillWait).ConfigureAwait(false);
    }

    /// <summary>Ends the program and everything below it at once.</summary>
    public void Kill()
    {
        Interlocked.Exchange(ref _stopping, 1);
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone, or ending.
        }

        KillLeftovers();
        Interlocked.Exchange(ref _job, null)?.Dispose();
    }

    public void Dispose()
    {
        if (!HasExited)
        {
            Kill();
        }

        _input.Dispose();
        _pumps.Dispose();
        _process.Dispose();
    }

    private void Signal(int signal, IReadOnlyList<int> below)
    {
        UnixSignals.Send(Pid, signal);
        foreach (var pid in below)
        {
            UnixSignals.Send(pid, signal);
        }
    }

    private async Task<bool> WaitAsync(TimeSpan wait)
    {
        try
        {
            await _exited.Task.WaitAsync(wait).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task MonitorAsync(TimeSpan? maxLifetime)
    {
        try
        {
            using var limit = maxLifetime is { } max ? new CancellationTokenSource(max) : new CancellationTokenSource();
            try
            {
                await _process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LifetimeEnded = true;
                Kill();
                using var wait = new CancellationTokenSource(KillWait);
                try
                {
                    await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The kill was issued; the process did not confirm in time.
                }
            }

            // Everything the program wrote is read. A stray child that still holds the pipes is ended instead of waited for.
            if (await Task.WhenAny(_readers, Task.Delay(DrainGrace)).ConfigureAwait(false) != _readers)
            {
                KillLeftovers();
                Interlocked.Exchange(ref _job, null)?.Dispose();
                if (await Task.WhenAny(_readers, Task.Delay(DrainGrace)).ConfigureAwait(false) != _readers)
                {
                    await _pumps.CancelAsync().ConfigureAwait(false);
                    await Task.WhenAny(_readers, Task.Delay(DrainGrace)).ConfigureAwait(false);
                }
            }

            // Children that outlived the program (a script that started a server in the background and exited) are ended with it.
            KillLeftovers();
            Interlocked.Exchange(ref _job, null)?.Dispose();
        }
        catch (Exception ex)
        {
            Waymark.Core.LiveFeed.FailureReporter.Report(ex, "Watching a program that runs in a terminal");
        }
        finally
        {
            _exited.TrySetResult(ReadExitCode());
        }
    }

    private void KillLeftovers()
    {
        foreach (var (pid, started) in _descendants)
        {
            if (ProcessTree.IsSameProcess(pid, started))
            {
                ProcessTree.KillTree(pid);
            }
        }
    }

    private int? ReadExitCode()
    {
        try
        {
            return _process.HasExited ? _process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
