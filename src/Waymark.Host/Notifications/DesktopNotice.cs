namespace Waymark.Host.Notifications;

/// <summary>
/// One desktop notification: a short title, one or two sentences, and the admin page that a click opens (Windows only; macOS
/// notifications from a script cannot open a link, so the message itself says where to go).
/// </summary>
public sealed record DesktopNotice(string Title, string Message, Uri OpenUri);

/// <summary>
/// Shows a notification on this computer's desktop. It only tells the owner that something waits: approving stays in the admin UI.
/// </summary>
public interface IDesktopNotifier
{
    /// <summary>False where this system has no notifier (Linux), so the admin UI can say so instead of offering a switch that does nothing.</summary>
    bool IsSupported { get; }

    /// <summary>Shows the notice. Throws when the operating system refused it or did not answer in time.</summary>
    Task ShowAsync(DesktopNotice notice, CancellationToken ct);
}
