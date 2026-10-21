using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace Waymark.Host.Auth;

public sealed record OAuthErrorBody(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorDescription);

/// <summary>JSON the OAuth clients read: never cached, never stored by intermediaries.</summary>
internal sealed class JsonNoStoreResult(int status, object body) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        response.StatusCode = status;
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        return response.WriteAsJsonAsync(body, body.GetType(), cancellationToken: httpContext.RequestAborted);
    }
}

/// <summary>An HTML page with its own strict content security policy.</summary>
internal sealed class HtmlResult(int status, string html, string contentSecurityPolicy) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        response.StatusCode = status;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers["Content-Security-Policy"] = contentSecurityPolicy;
        return response.WriteAsync(html, httpContext.RequestAborted);
    }
}

internal static class OAuthResults
{
    public static IResult Error(int status, string error, string? description = null) =>
        new JsonNoStoreResult(status, new OAuthErrorBody(error, description));

    public static IResult Json(int status, object body) => new JsonNoStoreResult(status, body);

    /// <summary>Sends the browser back to the client with the given parameters; null values are left out.</summary>
    public static IResult Redirect(string redirectUri, IReadOnlyDictionary<string, string?> parameters)
    {
        var query = new Dictionary<string, string?>();
        foreach (var (key, value) in parameters)
        {
            if (value is not null)
            {
                query[key] = value;
            }
        }

        return Results.Redirect(QueryHelpers.AddQueryString(redirectUri, query));
    }

    public static IResult RedirectError(string redirectUri, string error, string description, string? state) =>
        Redirect(redirectUri, new Dictionary<string, string?>
        {
            ["error"] = error,
            ["error_description"] = description,
            ["state"] = state
        });
}
