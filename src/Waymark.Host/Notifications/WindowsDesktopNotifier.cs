using System.Security;

namespace Waymark.Host.Notifications;

/// <summary>
/// Shows a Windows toast through Windows PowerShell 5.1, which can load the Windows Runtime toast types without any package or
/// Windows-only build of Waymark. The toast is sent under PowerShell's own app id, so Windows shows it as coming from Windows
/// PowerShell. Clicking it, or its button, opens the admin page in the default browser. The script is fixed text: only the
/// title, the message and the address are put into it, XML-escaped and quoted, and it is passed on standard input rather than
/// the command line.
/// </summary>
public sealed class WindowsDesktopNotifier : IDesktopNotifier
{
    /// <summary>All of Waymark's approval toasts share this tag, so a new one replaces the previous one instead of piling up.</summary>
    internal const string Tag = "waymark-approval";

    internal const string Group = "waymark";

    // The app id under which Windows PowerShell shows notifications. It exists on every Windows 10 and 11 installation.
    internal const string PowerShellAppId = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe";

    // Windows PowerShell can take a few seconds to start on a busy or cold machine.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // "-Command -" reads the script from standard input, so nothing of it is on the command line.
    private static readonly string[] Arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "-"];

    public bool IsSupported => true;

    public Task ShowAsync(DesktopNotice notice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return NotifierProcess.RunAsync(PowerShellPath(), Arguments, BuildScript(notice), Timeout, ct);
    }

    /// <summary>
    /// The full path of Windows PowerShell 5.1. PowerShell 7 (pwsh) cannot load Windows Runtime types, so the program is not looked
    /// up on PATH, where pwsh may come first.
    /// </summary>
    internal static string PowerShellPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// One line of PowerShell, so that reading it from standard input cannot split a block. Any failure is written to standard error
    /// and ends the program with exit code 1.
    /// </summary>
    internal static string BuildScript(DesktopNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var url = Xml(notice.OpenUri.AbsoluteUri);
        var toast =
            "<toast activationType=\"protocol\" launch=\"" + url + "\">" +
            "<visual><binding template=\"ToastGeneric\">" +
            "<text>" + Xml(notice.Title) + "</text>" +
            "<text>" + Xml(notice.Message) + "</text>" +
            "</binding></visual>" +
            "<actions><action content=\"Open Waymark\" activationType=\"protocol\" arguments=\"" + url + "\" /></actions>" +
            "</toast>";

        return
            "$ErrorActionPreference = 'Stop'; try { " +
            "[void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]; " +
            "[void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]; " +
            "$xml = New-Object Windows.Data.Xml.Dom.XmlDocument; " +
            "$xml.LoadXml(" + PowerShellLiteral(toast) + "); " +
            "$toast = New-Object Windows.UI.Notifications.ToastNotification $xml; " +
            "$toast.Tag = " + PowerShellLiteral(Tag) + "; " +
            "$toast.Group = " + PowerShellLiteral(Group) + "; " +
            "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier(" + PowerShellLiteral(PowerShellAppId) + ").Show($toast); " +
            "exit 0 } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }" +
            "\r\n";
    }

    private static string Xml(string text) => SecurityElement.Escape(text) ?? string.Empty;

    // A single-quoted PowerShell string expands nothing; a single quote inside it is written twice.
    private static string PowerShellLiteral(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
}
