using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Waymark.Host.Notifications;

/// <summary>
/// Starts the small operating-system program that shows a notification (Windows PowerShell or osascript) and waits for it, with a
/// time limit. This deliberately does not go through the command coordinator: a notification about an approval must not itself
/// wait for an approval or appear in the console.
/// </summary>
internal static class NotifierProcess
{
    private const int MaxErrorChars = 300;

    /// <param name="standardInput">Text written to the program's input and then closed, or null for no input.</param>
    /// <exception cref="TimeoutException">The program did not finish in time; it was stopped.</exception>
    /// <exception cref="InvalidOperationException">The program did not start or ended with an error.</exception>
    public static async Task RunAsync(string fileName, IReadOnlyList<string> arguments, string? standardInput, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var name = Path.GetFileName(fileName);
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
        };

        if (standardInput is not null)
        {
            // No byte order mark: PowerShell would read it as part of the first command.
            start.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException($"{name} did not start.");
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);

        // Both streams are read while the program runs, so a full pipe can never block it.
        var output = process.StandardOutput.ReadToEndAsync(linked.Token);
        var error = process.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            if (standardInput is not null)
            {
                try
                {
                    await process.StandardInput.WriteAsync(standardInput.AsMemory(), linked.Token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(linked.Token).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The program ended before it read its input; its exit code and error text below say why.
                }
                finally
                {
                    process.StandardInput.Close();
                }
            }

            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await Settle(output, error).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException(string.Create(CultureInfo.InvariantCulture, $"{name} did not finish within {timeout.TotalSeconds:0} seconds, so it was stopped."));
        }

        await Settle(output, error).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (process.ExitCode != 0)
        {
            var detail = error.IsCompletedSuccessfully ? Shorten(error.Result) : string.Empty;
            var suffix = detail.Length == 0 ? string.Empty : ": " + detail;
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{name} ended with exit code {process.ExitCode}{suffix}"));
        }
    }

    // Typed as plain tasks on purpose: SuppressThrowing is only allowed on a Task, and WhenAll of two Task<string> would be a Task<string[]>.
    private static Task Settle(Task output, Task error) => Task.WhenAll(output, error);

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It ended on its own in the meantime.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // It could not be stopped; it ends by itself, and nothing waits for it any more.
        }
    }

    private static string Shorten(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= MaxErrorChars ? trimmed : trimmed[..MaxErrorChars] + "...";
    }
}
