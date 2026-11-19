using Waymark.Host.Notifications;

namespace Waymark.Tests.Notifications;

public sealed class DesktopNotifierScriptTests
{
    private static readonly DesktopNotice Hostile = new(
        "Title with 'quotes' & <tags>",
        "Message with \"double\" quotes; $(Get-Process) and `backticks`",
        new Uri("http://localhost:47100/console"));

    [Fact]
    public void TheWindowsScriptIsOneLineThatEndsWithANewLine()
    {
        var script = WindowsDesktopNotifier.BuildScript(Hostile);

        Assert.EndsWith("\r\n", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", script.TrimEnd('\r', '\n'), StringComparison.Ordinal);
    }

    [Fact]
    public void TheWindowsScriptEscapesTheTextForXmlAndPowerShell()
    {
        var script = WindowsDesktopNotifier.BuildScript(Hostile);

        Assert.Contains("Title with &apos;quotes&apos; &amp; &lt;tags&gt;", script, StringComparison.Ordinal);
        Assert.Contains("&quot;double&quot;", script, StringComparison.Ordinal);
        Assert.DoesNotContain("'quotes'", script, StringComparison.Ordinal);

        // The text sits inside one single-quoted PowerShell string, so $(...) and backticks are not expanded.
        var start = script.IndexOf("$xml.LoadXml('", StringComparison.Ordinal);
        var end = script.IndexOf("'); ", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var literal = script[(start + "$xml.LoadXml('".Length)..end];
        Assert.DoesNotContain("'", literal.Replace("''", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("$(Get-Process)", literal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWindowsToastOpensTheAdminPage()
    {
        var script = WindowsDesktopNotifier.BuildScript(Hostile);

        Assert.Contains("launch=\"http://localhost:47100/console\"", script, StringComparison.Ordinal);
        Assert.Contains(WindowsDesktopNotifier.Tag, script, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePowerShellPathIsWindowsPowerShellNotPwsh()
    {
        Assert.EndsWith(Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe"), WindowsDesktopNotifier.PowerShellPath(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheMacTextIsPassedAsArgumentsNotPartOfTheScript()
    {
        var arguments = MacDesktopNotifier.BuildArguments(Hostile);

        Assert.Equal(Hostile.Title, arguments[^2]);
        Assert.Equal(Hostile.Message, arguments[^1]);
        Assert.DoesNotContain(arguments.Take(arguments.Count - 2), argument => argument.Contains("quotes", StringComparison.Ordinal));
    }
}
