using System.Diagnostics;
using Waymark.Core.LiveFeed;

namespace Waymark.Host.Edge;

/// <summary>
/// Shows the requests that matter for signing in on the Live page: calls to the sign-in endpoints, refused calls to the MCP listener,
/// failed discovery requests and any server error. Page loads of the admin UI and successful MCP calls are not shown (tool calls have
/// their own lines). It runs first, so requests the listener guard refuses are seen too. Only the path is logged, never the query
/// string: that is where the authorization code and pairing secret travel.
/// </summary>
public sealed class RequestLoggingMiddleware(RequestDelegate next, IListenerClassifier classifier, ILogger<RequestLoggingMiddleware> logger)
{
    private static readonly string[] SignInPaths = ["/authorize", "/token", "/revoke", "/register"];

    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var isAdmin = classifier.Classify(context) == ListenerKind.Admin;
            var status = context.Response.StatusCode;
            if (IsWorthShowing(context.Request.Path, isAdmin, status))
            {
                logger.LogAuthRequest(
                    isAdmin ? "admin" : "mcp",
                    context.Request.Method,
                    context.Request.Path.Value ?? "/",
                    status,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
    }

    private static bool IsWorthShowing(PathString path, bool isAdmin, int status)
    {
        if (status >= 500)
        {
            return true;
        }

        foreach (var signIn in SignInPaths)
        {
            if (path.StartsWithSegments(signIn))
            {
                return true;
            }
        }

        if (path.StartsWithSegments("/.well-known"))
        {
            return status >= 400;
        }

        // A refused or throttled call from outside: a bad or missing token, or too many requests.
        return !isAdmin && status is 401 or 403 or 429;
    }
}
