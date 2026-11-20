using Waymark.Core.Memory;
using Waymark.Host.Mcp;

namespace Waymark.Tests.Mcp;

public sealed class CallerContextTests
{
    [Fact]
    public async Task TheCallerIsVisibleInsideAnAwaitedHandler()
    {
        var callers = new CallerContext();
        string? seen;
        using (callers.Enter("client-7"))
        {
            seen = await Task.Run(async () =>
            {
                await Task.Yield();
                return callers.Client;
            }, TestContext.Current.CancellationToken);
        }

        Assert.Equal("client-7", seen);
    }

    [Fact]
    public void TheCallerIsClearedAfterTheCall()
    {
        var callers = new CallerContext();
        using (callers.Enter("client-7"))
        {
            Assert.Equal("client-7", callers.Client);
        }

        Assert.Equal(CallerContext.Unknown, callers.Client);
    }

    [Fact]
    public void TheAuthorIsAClaudeClientWithTheClientId()
    {
        var callers = new CallerContext();
        using var scope = callers.Enter("client-7");

        var author = callers.AsModelAuthor();

        Assert.Equal(NoteAuthorKind.ClaudeClient, author.Kind);
        Assert.Equal("client-7", author.Id);
    }

    [Fact]
    public async Task TwoCallsDoNotSeeEachOther()
    {
        var callers = new CallerContext();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> RunAsync(string client)
        {
            using (callers.Enter(client))
            {
                await gate.Task;
                return callers.Client;
            }
        }

        var first = RunAsync("one");
        var second = RunAsync("two");
        gate.SetResult();

        Assert.Equal("one", await first);
        Assert.Equal("two", await second);
    }

    [Theory]
    [InlineData("client-7", "Claude Desktop", "client-7")]
    [InlineData(null, "Claude Desktop", "stdio:Claude Desktop")]
    [InlineData("", " claude-code ", "stdio:claude-code")]
    [InlineData(null, null, "unknown")]
    [InlineData(null, "  ", "unknown")]
    public void TheClientComesFromTheTokenThenTheStdioName(string? claim, string? stdioName, string expected) =>
        Assert.Equal(expected, CallerContext.ClientOf(claim, stdioName));
}
