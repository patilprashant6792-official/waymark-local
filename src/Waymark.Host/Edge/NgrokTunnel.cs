using System.Globalization;
using Waymark.Core.Edge;
using Waymark.Core.Events;
using Waymark.Logging;

namespace Waymark.Host.Edge;

/// <summary>
/// Owns the ngrok tunnel: makes sure the agent is installed, starts it for the saved address with the saved
/// authtoken, and reports each step. If an ngrok for the same address is already running (for example left over
/// from an earlier run) it is used as it is instead of starting a second one.
/// </summary>
public sealed class NgrokTunnel(
    NgrokLocator locator,
    NgrokInstaller installer,
    NgrokAgentClient agent,
    INgrokAuthStore auth,
    IEventBus events,
    EdgeOptions edge,
    NgrokOptions options,
    ILogger<NgrokTunnel> logger) : IDisposable
{
    private const int MaxMessageLength = 300;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ManagedProcess? _process;
    private TunnelStatus _status = TunnelStatus.Stopped;
    private volatile bool _held;

    public TunnelStatus Status => Volatile.Read(ref _status);

    /// <summary>True after the user stopped the tunnel on purpose; the background check then leaves it alone.</summary>
    public bool IsHeld => _held;

    /// <summary>
    /// Brings the tunnel for <paramref name="origin"/> online and returns the outcome; it never throws for an
    /// expected failure. <paramref name="allowInstall"/> is true only for an explicit save by the user.
    /// </summary>
    public async Task<TunnelStatus> EnsureAsync(string origin, bool allowInstall, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        await _gate.WaitAsync(ct);
        try
        {
            return await EnsureCoreAsync(origin, allowInstall, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends the ngrok process that Waymark started. An ngrok that Waymark did not start is left alone.</summary>
    public async Task StopAsync()
    {
        await StopOwnedAsync();
        await SetAsync(TunnelPhase.Stopped, "The tunnel is not running.", null);
    }

    /// <summary>
    /// Stops the tunnel and keeps it stopped: the background check will not start it again until <see cref="Resume"/>
    /// is called (an explicit save or start by the user) or Waymark restarts.
    /// </summary>
    public async Task HoldAsync(CancellationToken ct)
    {
        _held = true;
        await _gate.WaitAsync(ct);
        try
        {
            await StopOwnedAsync();
            var remaining = await agent.DetectAsync(ct);
            var message = remaining.Url is { } url
                ? $"Waymark's tunnel is stopped, but another ngrok is still running for {url}. End ngrok.exe (Task Manager) to free that address."
                : "The tunnel is stopped.";
            await SetAsync(TunnelPhase.Stopped, message, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lets the background check start the tunnel again.</summary>
    public void Resume() => _held = false;

    public void Dispose()
    {
        Interlocked.Exchange(ref _process, null)?.Dispose();
        _gate.Dispose();
    }

    private async Task<TunnelStatus> EnsureCoreAsync(string origin, bool allowInstall, CancellationToken ct)
    {
        var detected = await agent.DetectAsync(ct);
        if (IsSameAddress(detected.Url, origin))
        {
            return await SetAsync(
                TunnelPhase.Online,
                _process is null ? "Connected to the ngrok that is already running for this address." : "The tunnel is online.",
                origin);
        }

        var hadOwnProcess = _process is not null;
        await StopOwnedAsync();
        if (!hadOwnProcess && detected.Url is { } other)
        {
            return await SetAsync(TunnelPhase.Failed, $"ngrok is already running for {other}. Close that ngrok, or save that address instead.", origin);
        }

        var executable = locator.Find();
        if (executable is null)
        {
            if (!allowInstall)
            {
                return await SetAsync(TunnelPhase.Failed, "ngrok is not installed. Save the address on the Setup page to install it.", origin);
            }

            await SetAsync(TunnelPhase.Installing, "Installing ngrok (this can take a minute)...", origin);
            var install = await installer.InstallAsync(ct);
            executable = install.Installed ? locator.Find() : null;
            if (executable is null)
            {
                return await SetAsync(TunnelPhase.Failed, install.Message, origin);
            }
        }

        var authtoken = await auth.GetAsync(ct);
        if (authtoken is null)
        {
            return await SetAsync(TunnelPhase.Failed, "Enter your ngrok authtoken (dashboard.ngrok.com, Your Authtoken) and save again.", origin);
        }

        return await StartAsync(executable, authtoken, origin, ct);
    }

    private async Task<TunnelStatus> StartAsync(string executable, string authtoken, string origin, CancellationToken ct)
    {
        await SetAsync(TunnelPhase.Starting, "Starting ngrok...", origin);

        string[] arguments =
        [
            "http",
            edge.McpPort.ToString(CultureInfo.InvariantCulture),
            "--url",
            origin,
            "--log=stdout",
            "--log-format=logfmt"
        ];
        var environment = new Dictionary<string, string> { ["NGROK_AUTHTOKEN"] = authtoken };

        ManagedProcess started;
        try
        {
            started = ManagedProcess.Start(
                executable,
                arguments,
                environment,
                line => LogScrubber.Scrub(line).Replace(authtoken, LogScrubber.Hidden, StringComparison.Ordinal),
                line => logger.LogDebug("ngrok: {Line}", line));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return await SetAsync(TunnelPhase.Failed, $"ngrok could not be started: {ex.Message}", origin);
        }

        _process = started;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.StartTimeout);
        try
        {
            while (true)
            {
                if (started.HasExited)
                {
                    var reason = Explain(started);
                    await StopOwnedAsync();
                    return await SetAsync(TunnelPhase.Failed, reason, origin);
                }

                var detected = await agent.DetectAsync(timeout.Token);
                if (IsSameAddress(detected.Url, origin))
                {
                    return await SetAsync(TunnelPhase.Online, "The tunnel is online.", origin);
                }

                await Task.Delay(PollInterval, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var last = LastLine(started);
            await StopOwnedAsync();
            return await SetAsync(
                TunnelPhase.Failed,
                $"ngrok did not come online within {options.StartTimeout.TotalSeconds:0} seconds.{(last is null ? string.Empty : " Last message: " + last)}",
                origin);
        }
        catch (OperationCanceledException)
        {
            await StopOwnedAsync();
            throw;
        }
    }

    private async Task StopOwnedAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null)
        {
            return;
        }

        await process.StopAsync();
        process.Dispose();
    }

    private async Task<TunnelStatus> SetAsync(TunnelPhase phase, string message, string? domain)
    {
        var next = new TunnelStatus(phase, message, domain);
        if (Volatile.Read(ref _status) == next)
        {
            return next;
        }

        Volatile.Write(ref _status, next);
        logger.Log(
            phase == TunnelPhase.Failed ? LogLevel.Warning : LogLevel.Information,
            "ngrok tunnel {Phase}: {Message}",
            phase,
            message);
        await events.PublishAsync(new TunnelStatusChanged(next));
        return next;
    }

    private static bool IsSameAddress(string? detected, string origin) =>
        detected is not null && string.Equals(detected.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Prefers ngrok's own coded error (ERR_NGROK_...), which says exactly what to fix; otherwise the last thing it printed.</summary>
    private static string Explain(ManagedProcess process)
    {
        var lines = process.RecentLines;
        var coded = lines.LastOrDefault(line => line.Contains("ERR_NGROK_", StringComparison.OrdinalIgnoreCase));
        var text = coded ?? LastLine(process) ?? "it printed nothing";
        return $"ngrok stopped (exit code {process.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}): {Trim(text)}";
    }

    private static string? LastLine(ManagedProcess process) =>
        process.RecentLines.LastOrDefault(line => !string.IsNullOrWhiteSpace(line)) is { } line ? Trim(line) : null;

    private static string Trim(string text) =>
        text.Length <= MaxMessageLength ? text : string.Concat(text.AsSpan(0, MaxMessageLength), "...");
}
