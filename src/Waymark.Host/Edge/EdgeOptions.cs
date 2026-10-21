using System.Globalization;

namespace Waymark.Host.Edge;

/// <summary>The two loopback ports: the admin UI and the MCP/OAuth listener that ngrok forwards to.</summary>
public sealed record EdgeOptions(int AdminPort, int McpPort)
{
    public const int DefaultAdminPort = 47100;
    public const int DefaultMcpPort = 47101;

    public static EdgeOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var admin = ReadPort(configuration["Waymark:AdminPort"], DefaultAdminPort, "Waymark:AdminPort");
        var mcp = ReadPort(configuration["Waymark:McpPort"], DefaultMcpPort, "Waymark:McpPort");
        if (admin == mcp)
        {
            throw new InvalidOperationException("Waymark:AdminPort and Waymark:McpPort must be different ports.");
        }

        return new EdgeOptions(admin, mcp);
    }

    private static int ReadPort(string? value, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
        {
            throw new InvalidOperationException($"{name} must be a port between 1024 and 65535.");
        }

        return port;
    }
}
