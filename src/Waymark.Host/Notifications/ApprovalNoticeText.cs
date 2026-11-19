using System.Globalization;

namespace Waymark.Host.Notifications;

/// <summary>
/// The words of the approval notification. It says only how many requests wait and where to decide, never what they are: a
/// notification can show on the lock screen and stays in the notification history.
/// </summary>
public static class ApprovalNoticeText
{
    public const string Title = "Waymark needs your approval";

    /// <summary>The notice for what waits, or null when nothing waits.</summary>
    public static DesktopNotice? For(PendingDecisions pending, int adminPort)
    {
        if (pending.Total <= 0)
        {
            return null;
        }

        var parts = new List<string>(2);
        if (pending.Commands > 0)
        {
            parts.Add(Count(pending.Commands, "command", "commands"));
        }

        if (pending.Pairings > 0)
        {
            parts.Add(Count(pending.Pairings, "app connection", "app connections"));
        }

        // Commands first: they hold up the agent that is working right now.
        var (path, page) = pending.Commands > 0 ? ("console", "Console") : ("approvals", "Approvals");
        var verb = pending.Total == 1 ? "is" : "are";
        var host = Host(adminPort);
        var message = $"{string.Join(" and ", parts)} {verb} waiting. Open the {page} page of Waymark at {host} to decide.";
        return new DesktopNotice(Title, message, new Uri($"http://{host}/{path}"));
    }

    /// <summary>The notice the Setup page sends to check that notifications reach the desktop.</summary>
    public static DesktopNotice Test(int adminPort)
    {
        var host = Host(adminPort);
        return new DesktopNotice(
            "Waymark test notification",
            $"Notifications work. When something waits for your approval, a notice like this tells you to open Waymark at {host}.",
            new Uri($"http://{host}/setup"));
    }

    private static string Host(int adminPort) => "localhost:" + adminPort.ToString(CultureInfo.InvariantCulture);

    private static string Count(int count, string one, string many) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);
}
