using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Waymark.Core.LiveFeed;

namespace Waymark.Modules.Commands.Execution;

/// <summary>What to start: a program and its arguments as a list (no shell reads them), the folder, the environment and the longest it may run.</summary>
public sealed record RunSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout);

/// <param name="ExitCode">Null when the program never started or did not report one.</param>
/// <param name="StartFailure">Why the program could not be started, or null.</param>
public sealed record RunResult(int? ExitCode, bool TimedOut, bool Cancelled, string? StartFailure);

/// <summary>
/// Starts one program and streams what it prints. Input is closed so nothing can wait for a key press, the time limit and the stop signal
/// end the whole process tree, and a child that keeps the output pipes open after the program ended cannot keep the run from finishing.
/// </summary>
public sealed class ProcessRunner
{
    private const int BufferChars = 4096;
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);

    public async Task<RunResult> RunAsync(RunSpec spec, Action<string> onText, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(onText);

        var info = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = spec.WorkingDirectory,
        };
        foreach (var argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Clear();
        foreach (var (key, value) in spec.Environment)
        {
            info.Environment[key] = value;
        }

        using var process = new Process { StartInfo = info };
        using var job = ProcessJob.TryCreate();
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new RunResult(null, false, false, $"Could not start '{spec.FileName}': {ex.Message}");
        }

        job?.Assign(process);
        CloseInput(process);

        var gate = new object();
        void Emit(string text)
        {
            lock (gate)
            {
                onText(text);
            }
        }

        using var pumps = new CancellationTokenSource();
        var readers = Task.WhenAll(Pump(process.StandardOutput, Emit, pumps.Token), Pump(process.StandardError, Emit, pumps.Token));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
        limit.CancelAfter(spec.Timeout);

        var timedOut = false;
        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = stop.IsCancellationRequested;
            timedOut = !cancelled;
            Kill(process, job);
            await WaitQuietlyAsync(process, KillWait).ConfigureAwait(false);
        }

        // Everything the program wrote is read. A stray child that still holds the pipes is ended instead of waited for.
        if (await Task.WhenAny(readers, Task.Delay(DrainGrace)).ConfigureAwait(false) != readers)
        {
            job?.Dispose();
            if (await Task.WhenAny(readers, Task.Delay(DrainGrace)).ConfigureAwait(false) != readers)
            {
                await pumps.CancelAsync().ConfigureAwait(false);
                await Task.WhenAny(readers, Task.Delay(DrainGrace)).ConfigureAwait(false);
            }
        }

        return new RunResult(ReadExitCode(process), timedOut, cancelled, null);
    }

    /// <summary>Runs a program to its end and returns what it printed. For short helper commands such as netstat.</summary>
    public async Task<(int? ExitCode, string Output)> CaptureAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var text = new StringBuilder();
        var spec = new RunSpec(fileName, arguments, System.Environment.CurrentDirectory, ChildEnvironment.Build(), timeout);
        var result = await RunAsync(spec, chunk => text.Append(chunk), ct).ConfigureAwait(false);
        return (result.ExitCode, result.StartFailure ?? text.ToString());
    }

    private static async Task Pump(StreamReader reader, Action<string> emit, CancellationToken ct)
    {
        var buffer = new char[BufferChars];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                // A failing handler must not stop the reading: the program would block on a full pipe and the run would hang until its time limit.
                try
                {
                    emit(new string(buffer, 0, read));
                }
                catch (Exception ex)
                {
                    FailureReporter.Report(ex, "Handling the output of a command");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped waiting for a pipe that a stray child keeps open.
        }
        catch (IOException)
        {
            // The pipe closed while reading.
        }
        catch (ObjectDisposedException)
        {
            // The process was disposed while reading.
        }
    }

    private static void CloseInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The program already closed its input.
        }
    }

    private static void Kill(Process process, ProcessJob? job)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (Win32Exception)
        {
            // Access denied or already ending; closing the job below ends the rest.
        }

        job?.Dispose();
    }

    private static async Task WaitQuietlyAsync(Process process, TimeSpan wait)
    {
        using var cts = new CancellationTokenSource(wait);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The kill was issued; the process did not confirm in time.
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    private static int? ReadExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
