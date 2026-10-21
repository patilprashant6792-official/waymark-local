using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Waymark.Core.LiveFeed;

namespace Waymark.Host.Edge;

/// <summary>
/// A child process that Waymark started: no shell, arguments passed as a list, output captured line by line
/// (after the caller's scrubbing) and the whole process tree ended on stop.
/// </summary>
public sealed class ManagedProcess : IDisposable
{
    private const int MaxRecentLines = 40;
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(5);

    private readonly Process _process;
    private readonly ConcurrentQueue<string> _recent = new();
    private readonly Func<string, string> _scrub;
    private readonly Action<string>? _onLine;

    private ManagedProcess(Process process, Func<string, string> scrub, Action<string>? onLine)
    {
        _process = process;
        _scrub = scrub;
        _onLine = onLine;
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int? ExitCode
    {
        get
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

    /// <summary>The last lines the process wrote, oldest first.</summary>
    public IReadOnlyList<string> RecentLines => [.. _recent];

    /// <exception cref="System.ComponentModel.Win32Exception">The file could not be started.</exception>
    public static ManagedProcess Start(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        Func<string, string> scrub,
        Action<string>? onLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(scrub);

        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment)
        {
            info.Environment[key] = value;
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var managed = new ManagedProcess(process, scrub, onLine);
        process.OutputDataReceived += managed.OnData;
        process.ErrorDataReceived += managed.OnData;
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return managed;
    }

    /// <summary>True when the process ended within the timeout. Cancelling the token throws.</summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            await _process.WaitForExitAsync(linked.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task StopAsync()
    {
        try
        {
            if (_process.HasExited)
            {
                return;
            }

            _process.Kill(entireProcessTree: true);
            using var wait = new CancellationTokenSource(StopWait);
            await _process.WaitForExitAsync(wait.Token);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (OperationCanceledException)
        {
            // Did not confirm within the wait; the kill was issued.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied or already ending; nothing more can be done from here.
        }
    }

    public void Dispose() => _process.Dispose();

    private void OnData(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        // This runs on a thread of the process machinery. A failure here would end Waymark, so it is reported and the line is dropped.
        try
        {
            var line = _scrub(e.Data);
            _recent.Enqueue(line);
            while (_recent.Count > MaxRecentLines && _recent.TryDequeue(out _))
            {
            }

            _onLine?.Invoke(line);
        }
        catch (Exception ex)
        {
            FailureReporter.Report(ex, "Reading the output of a helper program");
        }
    }
}
