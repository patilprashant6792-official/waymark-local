using Waymark.Logging;

namespace Waymark.Host.Edge;

public sealed record NgrokInstallResult(bool Installed, string Message);

/// <summary>Installs the ngrok agent from the Microsoft Store source with winget. Runs only when the user saves an address and ngrok is missing.</summary>
public sealed class NgrokInstaller(NgrokLocator locator, NgrokOptions options, ILogger<NgrokInstaller> logger)
{
    private const string DownloadPage = "https://ngrok.com/downloads/windows";

    private static readonly string[] WingetArguments =
    [
        "install", "ngrok", "--source", "msstore", "--silent", "--accept-package-agreements", "--accept-source-agreements"
    ];

    public async Task<NgrokInstallResult> InstallAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NgrokInstallResult(false, $"Waymark can only install ngrok automatically on Windows. Install it from {DownloadPage} and save again.");
        }

        var winget = NgrokLocator.FindExecutable("winget");
        if (winget is null)
        {
            return new NgrokInstallResult(false, $"winget was not found on this computer. Install ngrok from {DownloadPage} (or the Microsoft Store) and save again.");
        }

        logger.LogInformation("Installing ngrok with winget (this can take a minute).");
        ManagedProcess process;
        try
        {
            process = ManagedProcess.Start(winget, WingetArguments, new Dictionary<string, string>(), LogScrubber.Scrub, OnLine);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new NgrokInstallResult(false, $"winget could not be started: {ex.Message}");
        }

        using (process)
        {
            var finished = await process.WaitForExitAsync(options.InstallTimeout, ct);
            if (!finished)
            {
                await process.StopAsync();
                return new NgrokInstallResult(false, $"Installing ngrok took longer than {options.InstallTimeout.TotalMinutes:0} minutes and was stopped. Install it from {DownloadPage} and save again.");
            }

            // winget reports "already installed" and "needs a restart" through non-zero exit codes, so what counts is whether ngrok can now be found.
            if (locator.Find() is not null)
            {
                return new NgrokInstallResult(true, "ngrok is installed.");
            }

            var last = process.RecentLines.LastOrDefault(line => line.Length > 3) ?? "no output";
            return new NgrokInstallResult(false, $"winget finished (exit {process.ExitCode}) but ngrok was not found afterwards: {last}");
        }
    }

    private void OnLine(string line)
    {
        // winget draws spinners and progress bars as very short lines; they only add noise.
        if (line.Trim().Length > 3)
        {
            logger.LogInformation("winget: {Line}", line);
        }
    }
}
