using Waymark.Modules.Commands.Execution;

namespace Waymark.Tests.Commands;

public sealed class AnsiTextTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain text", "plain text")]
    [InlineData("\u001b[31mred\u001b[0m", "red")]
    [InlineData("\u001b[1;32;40mbold green\u001b[0m done", "bold green done")]
    [InlineData("a\u001b[2Kb", "ab")]
    [InlineData("a\u001b[?25lb\u001b[?25h", "ab")]
    [InlineData("\u001b]0;window title\u0007text", "text")]
    [InlineData("\u001b]8;;https://x\u001b\\link\u001b]8;;\u001b\\", "link")]
    public void StripRemovesTerminalCodes(string? input, string expected) =>
        Assert.Equal(expected, AnsiText.Strip(input!));

    [Fact]
    public void StripKeepsLineBreaksAndTabs() =>
        Assert.Equal("a\n\tb\r\n", AnsiText.Strip("a\n\t\u001b[0mb\r\n"));

    [Theory]
    [InlineData("50%\r75%\r100%", "100%")]
    [InlineData("line1\nprogress 1\rprogress 2\nend", "line1\nprogress 2\nend")]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\nb", "a\nb")]
    [InlineData("done\r", "done")]
    [InlineData("", "")]
    [InlineData("\u001b[32mok\u001b[0m\r\n", "ok\n")]
    public void PlainShowsWhatTheTerminalWouldShow(string input, string expected) =>
        Assert.Equal(expected, AnsiText.Plain(input));

    [Fact]
    public void PlainOfNullIsEmpty() =>
        Assert.Equal(string.Empty, AnsiText.Plain(null!));
}
