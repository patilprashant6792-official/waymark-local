using System.Globalization;
using Waymark.Core.Commands;
using Waymark.Host.Components.Shared;
using CoreTerminal = Waymark.Core.Commands.Terminal;

namespace Waymark.Host.Ui;

/// <summary>
/// How a terminal looks wherever the admin UI shows one (the header, the Console, the workspace cards and pages): its state chip, its short command,
/// its address and how long it runs, worded the same everywhere.
/// </summary>
public static class TerminalView
{
    /// <summary>The state chip: colour, word, and whether its dot pulses.</summary>
    public static (StatusState State, string Text, bool Live) Chip(CoreTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var waiting = terminal.State == TerminalState.Starting && terminal.Pid is null && !string.IsNullOrEmpty(terminal.StateNote);
        return terminal.State switch
        {
            TerminalState.Starting when waiting => (StatusState.Attention, "Waiting for you", false),
            TerminalState.Starting => (StatusState.Working, "Starting", true),
            TerminalState.Ready => (StatusState.Healthy, "Ready", false),
            TerminalState.AppDown => (StatusState.Attention, "App down", false),
            TerminalState.Crashed => (StatusState.Failed, terminal.ExitCode is { } code ? string.Create(CultureInfo.InvariantCulture, $"Crashed · exit {code}") : "Crashed", false),
            TerminalState.Exited => (StatusState.Off, "Ended", false),
            TerminalState.Stopped => (StatusState.Off, StoppedText(terminal.StoppedBy), false),
            TerminalState.WaymarkStopped => (StatusState.Off, "Ended: Waymark restarted", false),
            _ => (StatusState.Off, "Idle", false),
        };
    }

    /// <summary>The command without the script text npm commands carry for the approval card.</summary>
    public static string ShortCommand(string? commandLine, int max = 80)
    {
        var line = (commandLine ?? string.Empty).ReplaceLineEndings(" ").Trim();
        var cut = line.IndexOf("   # runs:", StringComparison.Ordinal);
        if (cut > 0)
        {
            line = line[..cut];
        }

        return line.Length > max ? line[..max] + "..." : line;
    }

    /// <summary>The first address, or the first port, or null.</summary>
    public static string? Address(CoreTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (terminal.Urls.Count > 0)
        {
            return terminal.Urls[0];
        }

        return terminal.Ports.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $":{terminal.Ports[0]}") : null;
    }

    /// <summary>The address without the scheme, for a short label (localhost:5173).</summary>
    public static string? ShortAddress(CoreTerminal terminal)
    {
        var address = Address(terminal);
        if (address is null)
        {
            return null;
        }

        var start = address.IndexOf("://", StringComparison.Ordinal);
        return (start >= 0 ? address[(start + 3)..] : address).TrimEnd('/');
    }

    /// <summary>A link target for the address, only for http(s) addresses.</summary>
    public static string? Link(CoreTerminal terminal)
    {
        var address = Address(terminal);
        return address is not null && address.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? address : null;
    }

    /// <summary>How long the program runs ("2h 13m"), or how long ago it ended ("3m ago"), or empty.</summary>
    public static string Age(CoreTerminal terminal, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (terminal.State.IsLive() && terminal.StartedUtc is { } started)
        {
            return Span(now - started);
        }

        return terminal.EndedUtc is { } ended ? Span(now - ended) + " ago" : string.Empty;
    }

    public static string Span(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalMinutes < 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalSeconds}s")
            : span.TotalHours < 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m")
            : span.TotalDays < 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h {span.Minutes}m")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalDays}d {span.Hours}h");
    }

    /// <summary>The marker class of a terminal card's left edge and border.</summary>
    public static string CardClass(CoreTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return terminal.State switch
        {
            TerminalState.Ready => "ok",
            TerminalState.Starting => terminal.Pid is null && !string.IsNullOrEmpty(terminal.StateNote) ? "warn" : "info",
            TerminalState.AppDown => "warn",
            TerminalState.Crashed => "err",
            _ => "off",
        };
    }

    private static string StoppedText(string? by) => by switch
    {
        TerminalActors.Owner => "Stopped by you",
        TerminalActors.Claude => "Stopped by Claude",
        _ => "Stopped",
    };
}
