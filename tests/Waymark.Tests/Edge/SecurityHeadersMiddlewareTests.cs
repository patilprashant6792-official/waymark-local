using Microsoft.AspNetCore.Http;
using Waymark.Host.Edge;

namespace Waymark.Tests.Edge;

public sealed class SecurityHeadersMiddlewareTests
{
    private sealed class FixedClassifier(ListenerKind kind) : IListenerClassifier
    {
        public ListenerKind Classify(HttpContext context) => kind;
    }

    private static async Task<(DefaultHttpContext Context, bool NextCalled)> RunAsync(ListenerKind kind)
    {
        var context = new DefaultHttpContext();
        var nextCalled = false;
        var middleware = new SecurityHeadersMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            new FixedClassifier(kind));

        await middleware.InvokeAsync(context);
        return (context, nextCalled);
    }

    [Theory]
    [InlineData(ListenerKind.Admin)]
    [InlineData(ListenerKind.Mcp)]
    public async Task EveryResponseGetsTheBasicHardeningHeaders(ListenerKind kind)
    {
        var (context, nextCalled) = await RunAsync(kind);

        Assert.True(nextCalled);
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions.ToString());
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
    }

    [Fact]
    public async Task TheAdminListenerAlsoGetsAReportOnlyContentSecurityPolicy()
    {
        var (context, _) = await RunAsync(ListenerKind.Admin);

        var policy = context.Response.Headers["Content-Security-Policy-Report-Only"].ToString();
        Assert.Contains("default-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("base-uri 'none'", policy, StringComparison.Ordinal);
        Assert.False(context.Response.Headers.ContainsKey("Content-Security-Policy"));
    }

    [Fact]
    public async Task TheMcpListenerGetsNoContentSecurityPolicy()
    {
        var (context, _) = await RunAsync(ListenerKind.Mcp);

        Assert.False(context.Response.Headers.ContainsKey("Content-Security-Policy-Report-Only"));
        Assert.False(context.Response.Headers.ContainsKey("Content-Security-Policy"));
    }
}
