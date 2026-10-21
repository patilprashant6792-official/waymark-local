using System.Text.Json;

namespace Waymark.Host.Edge;

public sealed record NgrokDetection(string? Url, string Message);

/// <summary>Reads the tunnel list that a running ngrok agent shows on its local inspection port.</summary>
public static class NgrokTunnelParser
{
    public static NgrokDetection Pick(string json, int mcpPort)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("tunnels", out var tunnels) || tunnels.ValueKind != JsonValueKind.Array)
            {
                return new NgrokDetection(null, "The ngrok agent answered, but its answer had no tunnel list.");
            }

            var https = new List<(string Url, string? Address)>();
            foreach (var tunnel in tunnels.EnumerateArray())
            {
                if (!tunnel.TryGetProperty("public_url", out var publicUrl)
                    || publicUrl.ValueKind != JsonValueKind.String
                    || publicUrl.GetString() is not { } url
                    || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? address = null;
                if (tunnel.TryGetProperty("config", out var config)
                    && config.ValueKind == JsonValueKind.Object
                    && config.TryGetProperty("addr", out var addr)
                    && addr.ValueKind == JsonValueKind.String)
                {
                    address = addr.GetString();
                }

                https.Add((url.TrimEnd('/'), address));
            }

            var matches = https.Where(t => t.Address is not null && t.Address.EndsWith($":{mcpPort}", StringComparison.Ordinal)).ToList();
            return matches.Count switch
            {
                1 => new NgrokDetection(matches[0].Url, "Found your ngrok address."),
                > 1 => new NgrokDetection(matches[0].Url, $"More than one tunnel forwards to port {mcpPort}; using the first."),
                _ when https.Count > 0 => new NgrokDetection(null, $"ngrok is running, but no tunnel forwards to port {mcpPort}. Start one with: ngrok http {mcpPort}"),
                _ => new NgrokDetection(null, $"ngrok is running but has no HTTPS tunnel yet. Start one with: ngrok http {mcpPort}")
            };
        }
        catch (JsonException)
        {
            return new NgrokDetection(null, "The ngrok agent's answer could not be read.");
        }
    }
}

public sealed class NgrokAgentClient(IHttpClientFactory httpClients, EdgeOptions edge)
{
    public const string HttpClientName = "waymark-ngrok-agent";

    private static readonly Uri TunnelsUri = new("http://127.0.0.1:4040/api/tunnels");

    /// <summary>Best effort: the agent API is optional and ngrok marks this endpoint as deprecated, so a failure just means "paste it yourself".</summary>
    public async Task<NgrokDetection> DetectAsync(CancellationToken ct)
    {
        try
        {
            using var client = httpClients.CreateClient(HttpClientName);
            using var response = await client.GetAsync(TunnelsUri, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new NgrokDetection(null, $"The ngrok agent answered with HTTP {(int)response.StatusCode}. Paste the address instead.");
            }

            return NgrokTunnelParser.Pick(await response.Content.ReadAsStringAsync(ct), edge.McpPort);
        }
        catch (HttpRequestException)
        {
            return new NgrokDetection(null, $"ngrok does not seem to be running. Start it with: ngrok http {edge.McpPort}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new NgrokDetection(null, "The ngrok agent did not answer in time. Paste the address instead.");
        }
    }
}
