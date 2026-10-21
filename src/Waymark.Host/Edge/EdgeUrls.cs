using Waymark.Core.Edge;

namespace Waymark.Host.Edge;

/// <summary>
/// Works out the base address the OAuth and MCP documents must advertise for a request. A request that arrived on
/// localhost gets localhost URLs (so Claude Code can be tested without a tunnel); anything else gets the saved public URL.
/// </summary>
public sealed class EdgeUrls(IPublicUrlProvider publicUrl)
{
    /// <summary>An origin with no trailing slash, or null when no public URL has been saved yet.</summary>
    public string? BaseFor(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return LoopbackHosts.IsLoopback(request.Host.Host)
            ? $"{request.Scheme}://{request.Host.Value}"
            : publicUrl.Current;
    }

    /// <summary>The protected resource, which must equal the URL entered in Claude exactly.</summary>
    public string? McpResourceFor(HttpRequest request) => BaseFor(request) is { } baseUrl ? baseUrl + "/mcp" : null;
}
