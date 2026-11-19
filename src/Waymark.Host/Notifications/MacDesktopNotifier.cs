namespace Waymark.Host.Notifications;

/// <summary>
/// Shows a macOS notification with osascript, which comes with every Mac. The title and message are passed as arguments to the
/// script's run handler, never pasted into the script, so no text can change what it does. macOS shows the notification as coming
/// from Script Editor and may ask once whether Script Editor may show notifications. Clicking it cannot open the admin UI, so the
/// message says where to go.
/// </summary>
public sealed class MacDesktopNotifier : IDesktopNotifier
{
    private const string OsaScript = "/usr/bin/osascript";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public bool IsSupported => true;

    public Task ShowAsync(DesktopNotice notice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return NotifierProcess.RunAsync(OsaScript, BuildArguments(notice), standardInput: null, Timeout, ct);
    }

    internal static IReadOnlyList<string> BuildArguments(DesktopNotice notice) =>
    [
        "-e", "on run argv",
        "-e", "display notification (item 2 of argv) with title (item 1 of argv)",
        "-e", "end run",
        notice.Title,
        notice.Message,
    ];
}
