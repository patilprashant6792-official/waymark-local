using Microsoft.AspNetCore.Http;
using Waymark.Host.Edge;

namespace Waymark.Tests.Edge;

public sealed class ListenerGuardTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private sealed class FixedClassifier(ListenerKind kind) : IListenerClassifier
    {
        public ListenerKind Classify(HttpContext context) => kind;
    }

    private static DefaultHttpContext Context(string host, string path, string method = "GET", string? origin = null, string? upgrade = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Request.Method = method;
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        if (upgrade is not null)
        {
            context.Request.Headers.Upgrade = upgrade;
        }

        context.Response.Body = new MemoryStream();
        return context;
    }

    private async Task<(bool NextCalled, int Status, string Body)> RunAsync(ListenerKind kind, DefaultHttpContext context)
    {
        var nextCalled = false;
        var guard = new ListenerGuard(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            new FixedClassifier(kind));

        await guard.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return (nextCalled, context.Response.StatusCode, await reader.ReadToEndAsync(_ct));
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/mcp/session")]
    [InlineData("/MCP")]
    [InlineData("/.well-known/oauth-authorization-server")]
    [InlineData("/register")]
    [InlineData("/authorize")]
    [InlineData("/token")]
    [InlineData("/revoke")]
    public async Task TheMcpListenerServesOnlyOAuthAndMcpPaths(string path)
    {
        var result = await RunAsync(ListenerKind.Mcp, Context("abc.ngrok-free.app", path, "POST"));

        Assert.True(result.NextCalled);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/admin")]
    [InlineData("/_blazor")]
    [InlineData("/mcpx")]
    [InlineData("/tokens")]
    [InlineData("/settings")]
    public async Task TheMcpListenerAnswers404ForEverythingElse(string path)
    {
        var result = await RunAsync(ListenerKind.Mcp, Context("abc.ngrok-free.app", path));

        Assert.False(result.NextCalled);
        Assert.Equal(StatusCodes.Status404NotFound, result.Status);
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/token")]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/register")]
    public async Task TheAdminListenerNeverServesTheMcpPaths(string path)
    {
        var result = await RunAsync(ListenerKind.Admin, Context("localhost:47100", path));

        Assert.False(result.NextCalled);
        Assert.Equal(StatusCodes.Status404NotFound, result.Status);
    }

    [Theory]
    [InlineData("localhost:47100")]
    [InlineData("LOCALHOST:47100")]
    [InlineData("127.0.0.1:47100")]
    [InlineData("[::1]:47100")]
    [InlineData("localhost")]
    public async Task TheAdminListenerAcceptsLoopbackHosts(string host)
    {
        var result = await RunAsync(ListenerKind.Admin, Context(host, "/"));

        Assert.True(result.NextCalled);
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("evil.example:47100")]
    [InlineData("localhost.evil.example")]
    [InlineData("127.0.0.2:47100")]
    [InlineData("192.168.1.5:47100")]
    [InlineData("abc.ngrok-free.app")]
    public async Task TheAdminListenerRefusesOtherHostsAgainstDnsRebinding(string host)
    {
        var result = await RunAsync(ListenerKind.Admin, Context(host, "/"));

        Assert.False(result.NextCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, result.Status);
        Assert.Equal("Invalid host.", result.Body);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task AChangingRequestFromAnotherOriginIsRefused(string method)
    {
        var result = await RunAsync(ListenerKind.Admin, Context("localhost:47100", "/settings", method, origin: "http://evil.example"));

        Assert.False(result.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
        Assert.Equal("Cross-origin request refused.", result.Body);
    }

    [Fact]
    public async Task AChangingRequestFromTheSamePageIsAllowed()
    {
        var result = await RunAsync(ListenerKind.Admin, Context("localhost:47100", "/settings", "POST", origin: "http://localhost:47100"));

        Assert.True(result.NextCalled);
    }

    [Fact]
    public async Task AChangingRequestOnAnotherPortOfTheSameHostIsRefused()
    {
        var result = await RunAsync(ListenerKind.Admin, Context("localhost:47100", "/settings", "POST", origin: "http://localhost:9999"));

        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
    }

    [Fact]
    public async Task AChangingRequestOverAnotherSchemeIsRefused()
    {
        var result = await RunAsync(ListenerKind.Admin, Context("localhost:47100", "/settings", "POST", origin: "https://localhost:47100"));

        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
    }

    [Fact]
    public async Task AChangingRequestWithoutAnOriginIsAllowed()
    {
        var result = await RunAsync(ListenerKind.Admin, Context("localhost:47100", "/settings", "POST"));

        Assert.True(result.NextCalled);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("not a url")]
    public async Task AnUnparseableOriginOnAChangingRequestIsRefused(string origin)
    {
        var result = await RunAsync(ListenerKind.Admin, Context("localhost:47100", "/settings", "POST", origin: origin));

        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task ReadOnlyRequestsAreNotOriginChecked(string method)
    {
        var result = await RunAsync(ListenerKind.Admin, Context("localhost:47100", "/", method, origin: "http://evil.example"));

        Assert.True(result.NextCalled);
    }

    [Fact]
    public async Task AWebSocketUpgradeFromAnotherOriginIsRefused()
    {
        var result = await RunAsync(
            ListenerKind.Admin,
            Context("localhost:47100", "/_blazor", "GET", origin: "http://evil.example", upgrade: "websocket"));

        Assert.False(result.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
    }

    [Fact]
    public async Task AWebSocketUpgradeFromTheSamePageIsAllowed()
    {
        var result = await RunAsync(
            ListenerKind.Admin,
            Context("localhost:47100", "/_blazor", "GET", origin: "http://localhost:47100", upgrade: "websocket"));

        Assert.True(result.NextCalled);
    }

    [Fact]
    public async Task TheMcpListenerDoesNotApplyTheAdminHostOrOriginRules()
    {
        var result = await RunAsync(
            ListenerKind.Mcp,
            Context("abc.ngrok-free.app", "/mcp", "POST", origin: "https://claude.ai"));

        Assert.True(result.NextCalled);
    }
}
