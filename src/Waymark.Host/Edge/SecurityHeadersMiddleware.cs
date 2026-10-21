namespace Waymark.Host.Edge;

/// <summary>
/// Hardening headers on every response. The admin UI gets the design-doc content security policy in report-only
/// mode first, because Blazor and MudBlazor must be checked against it in a real browser before it is enforced.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IListenerClassifier classifier)
{
    private const string AdminPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; " +
        "frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        if (classifier.Classify(context) == ListenerKind.Admin)
        {
            headers["Content-Security-Policy-Report-Only"] = AdminPolicy;
        }

        return next(context);
    }
}
