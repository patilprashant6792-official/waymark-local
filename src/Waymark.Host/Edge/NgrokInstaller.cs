using Waymark.Logging;

namespace Waymark.Host.Edge;

public sealed record NgrokInstallResult(bool Installed, string Message);

/// <summary>
/// Installs the ngrok agent: from the Microsoft Store source with winget on Windows, with Homebrew on macOS.
/// Runs only when the user saves an address and ngrok is missing.
/// </summary>
public sealed class NgrokInstaller(NgrokLocator locator, NgrokOptions options, ILogger<NgrokInstaller> logger)
{
    private const string WindowsDownloadPage = "https://ngrok.com/downloads/windows";
    private const string MacDownloadPage = "https://ngrok.com/downloads/mac-os";

    private static readonly string[] WingetArguments =
    [
        "install", "ngrok", "--source", "msstore", "--silent", "--accept-package-agreements", "--accept-source-agreements"
    ];

    private static readonly string[] BrewArguments = ["install", "ngrok"];

    // Homebrew must not stop to ask anything, and its hints only add noise to the log.
    private static readonly Dictionary<string, string> BrewEnvironment = new(StringComparer.Ordinal)
    {
        ["NONINTERACTIVE"] = "1",
        ["HOMEBREW_NO_ENV_HINTS"] = "1",
        ["HOMEBREW_NO_INSTALL_CLEANUP"] = "1",
    };

    public Task<NgrokInstallResult> InstallAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var winget = NgrokLocator.FindExecutable("winget");
            return winget is null
                ? Task.FromResult(new NgrokInstallResult(false, $"winget was not found on this computer. Install ngrok from {WindowsDownloadPage} (or the Microsoft Store) and save again."))
                : RunAsync("winget", winget, WingetArguments, new Dictionary<string, string>(), WindowsDownloadPage, ct);
        }

        if (OperatingSystem.IsMacOS())
        {
            var brew = NgrokLocator.FindExecutable("brew");
            return brew is null
                ? Task.FromResult(new NgrokInstallResult(false, $"Homebrew was not found on this computer. Install ngrok from {MacDownloadPage} and save again."))
                : RunAsync("Homebrew", brew, BrewArguments, BrewEnvironment, MacDownloadPage, ct);
        }

        return Task.FromResult(new NgrokInstallResult(false, "Waymark can install ngrok automatically on Windows and macOS only. Install it from https://ngrok.com/download and save again."));
    }

    private async Task<NgrokInstallResult> RunAsync(
        string installer, string program, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, string downloadPage, CancellationToken ct)
    {
        logger.LogInformation("Installing ngrok with {Installer} (this can take a minute).", installer);
        ManagedProcess process;
        try
        {
            process = ManagedProcess.Start(program, arguments, environment, LogScrubber.Scrub, line => OnLine(installer, line));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new NgrokInstallResult(false, $"{installer} could not be started: {ex.Message}");
        }

        using (process)
        {
            var finished = await process.WaitForExitAsync(options.InstallTimeout, ct);
            if (!finished)
            {
                await process.StopAsync();
                return new NgrokInstallResult(false, $"Installing ngrok took longer than {options.InstallTimeout.TotalMinutes:0} minutes and was stopped. Install it from {downloadPage} and save again.");
            }

            // winget and brew report "already installed" and "needs a restart" through non-zero exit codes, so what counts is whether ngrok can now be found.
            if (locator.Find() is not null)
            {
                return new NgrokInstallResult(true, "ngrok is installed.");
            }

            var last = process.RecentLines.LastOrDefault(line => line.Length > 3) ?? "no output";
            return new NgrokInstallResult(false, $"{installer} finished (exit {process.ExitCode}) but ngrok was not found afterwards: {last}");
        }
    }

    private void OnLine(string installer, string line)
    {
        // winget draws spinners and progress bars as very short lines; they only add noise.
        if (line.Trim().Length > 3)
        {
            logger.LogInformation("{Installer}: {Line}", installer, line);
        }
    }
}
