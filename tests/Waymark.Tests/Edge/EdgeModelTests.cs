using Microsoft.AspNetCore.Http;
using Waymark.Core.Edge;
using Waymark.Host.Edge;

namespace Waymark.Tests.Edge;

public sealed class EdgeModelTests
{
    private sealed class FixedPublicUrl(string? current) : IPublicUrlProvider
    {
        public string? Current => current;

        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static HttpRequest Request(string host, string scheme = "http")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        return context.Request;
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("LocalHost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public void RecognisesLoopbackHosts(string host) => Assert.True(LoopbackHosts.IsLoopback(host));

    [Theory]
    [InlineData("")]
    [InlineData("example.com")]
    [InlineData("127.0.0.2")]
    [InlineData("::1")]
    [InlineData("localhost.evil.com")]
    [InlineData("0.0.0.0")]
    public void RejectsEverythingElse(string host) => Assert.False(LoopbackHosts.IsLoopback(host));

    [Theory]
    [InlineData("/.well-known/oauth-authorization-server", true)]
    [InlineData("/.well-known", true)]
    [InlineData("/register", true)]
    [InlineData("/authorize", true)]
    [InlineData("/authorize/pair", true)]
    [InlineData("/token", true)]
    [InlineData("/revoke", true)]
    [InlineData("/mcp", true)]
    [InlineData("/Mcp/stream", true)]
    [InlineData("/", false)]
    [InlineData("/mcpx", false)]
    [InlineData("/tokens", false)]
    [InlineData("/registered", false)]
    [InlineData("/admin/mcp", false)]
    public void OnlyOAuthAndMcpPathsBelongToTheMcpListener(string path, bool expected) =>
        Assert.Equal(expected, ListenerPaths.IsMcpListenerPath(new PathString(path)));

    [Fact]
    public void TheAdminPortIsTheAdminListenerAndAnythingElseIsMcp()
    {
        var classifier = new PortListenerClassifier(new EdgeOptions(47100, 47101));
        var admin = new DefaultHttpContext();
        admin.Connection.LocalPort = 47100;
        var mcp = new DefaultHttpContext();
        mcp.Connection.LocalPort = 47101;
        var other = new DefaultHttpContext();
        other.Connection.LocalPort = 80;

        Assert.Equal(ListenerKind.Admin, classifier.Classify(admin));
        Assert.Equal(ListenerKind.Mcp, classifier.Classify(mcp));
        Assert.Equal(ListenerKind.Mcp, classifier.Classify(other));
    }

    [Fact]
    public void ALoopbackRequestGetsLoopbackUrls()
    {
        var urls = new EdgeUrls(new FixedPublicUrl("https://abc.ngrok-free.app"));
        var request = Request("localhost:47101");

        Assert.Equal("http://localhost:47101", urls.BaseFor(request));
        Assert.Equal("http://localhost:47101/mcp", urls.McpResourceFor(request));
    }

    [Fact]
    public void ATunnelledRequestGetsThePublicUrl()
    {
        var urls = new EdgeUrls(new FixedPublicUrl("https://abc.ngrok-free.app"));
        var request = Request("abc.ngrok-free.app", "https");

        Assert.Equal("https://abc.ngrok-free.app", urls.BaseFor(request));
        Assert.Equal("https://abc.ngrok-free.app/mcp", urls.McpResourceFor(request));
    }

    [Fact]
    public void WithoutASavedPublicUrlATunnelledRequestGetsNothing()
    {
        var urls = new EdgeUrls(new FixedPublicUrl(null));
        var request = Request("abc.ngrok-free.app", "https");

        Assert.Null(urls.BaseFor(request));
        Assert.Null(urls.McpResourceFor(request));
    }

    [Fact]
    public void ALoopbackRequestStillWorksWithoutASavedPublicUrl()
    {
        var urls = new EdgeUrls(new FixedPublicUrl(null));

        Assert.Equal("http://127.0.0.1:47101", urls.BaseFor(Request("127.0.0.1:47101")));
    }

    [Fact]
    public void ANullRequestIsRefused() =>
        Assert.Throws<ArgumentNullException>(() => new EdgeUrls(new FixedPublicUrl(null)).BaseFor(null!));
}
