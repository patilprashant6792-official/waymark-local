namespace Waymark.Host.Edge;

public enum ListenerKind
{
    Admin,
    Mcp
}

/// <summary>Tells which of the two listeners a request arrived on.</summary>
public interface IListenerClassifier
{
    ListenerKind Classify(HttpContext context);
}

/// <summary>Anything that did not arrive on the admin port is treated as the less privileged MCP listener.</summary>
public sealed class PortListenerClassifier(EdgeOptions options) : IListenerClassifier
{
    public ListenerKind Classify(HttpContext context) =>
        context.Connection.LocalPort == options.AdminPort ? ListenerKind.Admin : ListenerKind.Mcp;
}

public static class LoopbackHosts
{
    public static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "[::1]";
}

/// <summary>The only paths the MCP listener (and so the tunnel) may serve. The admin listener never serves them.</summary>
public static class ListenerPaths
{
    private static readonly PathString[] McpPrefixes = ["/.well-known", "/register", "/authorize", "/token", "/revoke", "/mcp"];

    public static bool IsMcpListenerPath(PathString path)
    {
        foreach (var prefix in McpPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
