namespace Waymark.Host.Edge;

/// <summary>
/// Keeps the two listeners apart. The MCP listener answers only OAuth and MCP paths, so the admin UI can never be
/// reached through the tunnel. The admin listener refuses any Host other than loopback (DNS rebinding) and any
/// browser request or WebSocket upgrade whose Origin is not the page's own origin.
/// </summary>
public sealed class ListenerGuard(RequestDelegate next, IListenerClassifier classifier)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var kind = classifier.Classify(context);
        var isMcpPath = ListenerPaths.IsMcpListenerPath(context.Request.Path);

        if (kind == ListenerKind.Mcp)
        {
            if (!isMcpPath)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }
        else
        {
            if (isMcpPath)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!LoopbackHosts.IsLoopback(context.Request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Invalid host.");
                return;
            }

            if (NeedsOriginCheck(context.Request) && !IsSameOrigin(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Cross-origin request refused.");
                return;
            }
        }

        await next(context);
    }

    private static bool NeedsOriginCheck(HttpRequest request) =>
        !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method)
        || request.Headers.Upgrade.Count > 0;

    /// <summary>A request without an Origin header (curl, the server itself) is allowed; a browser always sends one.</summary>
    private static bool IsSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
            && string.Equals(parsed.GetLeftPart(UriPartial.Authority), $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }
}
