using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Waymark.Core.Audit;
using Waymark.Core.Edge;
using Waymark.Core.Events;
using Waymark.Core.Settings;

namespace Waymark.Host.Edge;

public sealed record VerificationStep(string Name, bool Passed, string Detail);

public sealed record PublicUrlCheck(string? Origin, IReadOnlyList<VerificationStep> Steps)
{
    public bool Success => Origin is not null && Steps.All(s => s.Passed);
}

/// <summary>
/// Checks that a tunnel address really reaches this Waymark and, only then, saves it. Every step reports what it
/// found, so the Setup page can say which part failed instead of just "it did not work".
/// </summary>
public sealed class PublicUrlService(
    IHttpClientFactory httpClients,
    IConfigStore config,
    IPublicUrlProvider provider,
    IAuditStore audit,
    IEventBus events,
    INgrokAuthStore auth,
    NgrokTunnel tunnel,
    EdgeOptions edge,
    TimeProvider time)
{
    public const string HttpClientName = "waymark-public-url";

    private const string AddressStep = "Address";
    private const string AuthtokenStep = "ngrok authtoken";
    private const string TunnelProcessStep = "ngrok tunnel";
    private const string TunnelStep = "Tunnel reaches Waymark";
    private const string MetadataStep = "OAuth metadata";

    /// <summary>A tunnel that ngrok reports as online can need a moment before the public internet reaches it.</summary>
    private static readonly TimeSpan PropagationPause = TimeSpan.FromSeconds(2);

    public async Task<PublicUrlCheck> VerifyAsync(string input, CancellationToken ct)
    {
        var steps = new List<VerificationStep>();
        if (!PublicUrlRules.TryNormalize(input, out var origin, out var error))
        {
            steps.Add(new VerificationStep(AddressStep, false, error));
            return new PublicUrlCheck(null, steps);
        }

        steps.Add(new VerificationStep(AddressStep, true, origin));

        var instance = await InstanceIdAsync(ct);
        var tunnel = await GuardAsync(TunnelStep, () => ProbeInstanceAsync(origin, instance, ct), ct);
        steps.Add(tunnel);
        if (!tunnel.Passed)
        {
            return new PublicUrlCheck(origin, steps);
        }

        steps.Add(string.Equals(provider.Current, origin, StringComparison.OrdinalIgnoreCase)
            ? await GuardAsync(MetadataStep, () => ProbeMetadataAsync(origin, ct), ct)
            : new VerificationStep(MetadataStep, true, "Checked after the address is saved."));
        return new PublicUrlCheck(origin, steps);
    }

    public Task<PublicUrlCheck> VerifyAndSaveAsync(string input, string changedBy, CancellationToken ct) =>
        VerifyAndSaveAsync(input, null, changedBy, ct);

    /// <summary>
    /// Starts the tunnel for the address (installing ngrok first when it is missing), checks that the tunnel reaches this
    /// Waymark and, only then, saves the address. A pasted authtoken is saved encrypted before the tunnel starts.
    /// </summary>
    public async Task<PublicUrlCheck> VerifyAndSaveAsync(string input, string? authtoken, string changedBy, CancellationToken ct)
    {
        if (!PublicUrlRules.TryNormalize(input, out var origin, out var error))
        {
            return new PublicUrlCheck(null, [new VerificationStep(AddressStep, false, error)]);
        }

        var steps = new List<VerificationStep> { new(AddressStep, true, origin) };

        if (!string.IsNullOrWhiteSpace(authtoken))
        {
            if (!NgrokAuthRules.TryNormalize(authtoken, out var normalized, out var authError))
            {
                steps.Add(new VerificationStep(AuthtokenStep, false, authError));
                return new PublicUrlCheck(origin, steps);
            }

            await auth.SetAsync(normalized, changedBy, ct);
            await audit.AppendAsync(AuditEntries.Create(time, AuditCategory.Config, "config.ngrok_auth_changed", changedBy, SettingKeys.NgrokSecret), ct);
            steps.Add(new VerificationStep(AuthtokenStep, true, "Saved, encrypted."));
        }

        tunnel.Resume();
        var status = await tunnel.EnsureAsync(origin, allowInstall: true, ct);
        steps.Add(new VerificationStep(TunnelProcessStep, status.IsOnline, status.Message));
        if (!status.IsOnline)
        {
            return new PublicUrlCheck(origin, steps);
        }

        var reach = await VerifyAsync(origin, ct);
        if (!reach.Success)
        {
            await Task.Delay(PropagationPause, ct);
            reach = await VerifyAsync(origin, ct);
        }

        // The first step of the reach check is the address, which is already in the list.
        steps.AddRange(reach.Steps.Skip(1));
        var check = new PublicUrlCheck(origin, steps);
        if (!check.Success)
        {
            return check;
        }

        await config.SetAsync(SettingScope.Global, SettingKeys.PublicUrl, JsonSerializer.Serialize(origin), changedBy, ct);
        await config.SetAsync(
            SettingScope.Global,
            SettingKeys.PublicUrlVerifiedUtc,
            JsonSerializer.Serialize(time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)),
            changedBy,
            ct);
        await provider.RefreshAsync(ct);
        await audit.AppendAsync(AuditEntries.Create(time, AuditCategory.Config, "config.public_url_changed", changedBy, origin), ct);
        await events.PublishAsync(new PublicUrlChanged(origin));

        var metadata = await GuardAsync(MetadataStep, () => ProbeMetadataAsync(origin, ct), ct);
        return check with { Steps = [.. check.Steps.Where(s => s.Name != MetadataStep), metadata] };
    }

    /// <summary>Re-runs the checks against the saved address.</summary>
    public Task<PublicUrlCheck> VerifyCurrentAsync(CancellationToken ct) =>
        provider.Current is { } current
            ? VerifyAsync(current, ct)
            : Task.FromResult(new PublicUrlCheck(null, [new VerificationStep(AddressStep, false, "No address has been saved yet.")]));

    /// <summary>Stops the tunnel but keeps the saved address, so it can be started again from Setup. The tunnel stays off until then.</summary>
    public async Task StopTunnelAsync(string changedBy, CancellationToken ct)
    {
        await tunnel.HoldAsync(ct);
        await audit.AppendAsync(AuditEntries.Create(time, AuditCategory.Config, "tunnel.stopped", changedBy, provider.Current), ct);
    }

    /// <summary>Stops the tunnel and forgets the saved address, so Waymark does not start it again. The authtoken is kept.</summary>
    public async Task DisconnectAsync(string changedBy, CancellationToken ct)
    {
        var previous = provider.Current;
        await tunnel.HoldAsync(ct);
        await config.SetAsync(SettingScope.Global, SettingKeys.PublicUrl, JsonSerializer.Serialize<string?>(null), changedBy, ct);
        await config.SetAsync(SettingScope.Global, SettingKeys.PublicUrlVerifiedUtc, JsonSerializer.Serialize<string?>(null), changedBy, ct);
        await provider.RefreshAsync(ct);
        await audit.AppendAsync(AuditEntries.Create(time, AuditCategory.Config, "config.public_url_cleared", changedBy, previous), ct);
        await events.PublishAsync(new PublicUrlChanged(null));
    }

    private async Task<VerificationStep> ProbeInstanceAsync(string origin, string? expectedInstance, CancellationToken ct)
    {
        using var client = httpClients.CreateClient(HttpClientName);
        using var response = await client.GetAsync($"{origin}/.well-known/waymark-instance", ct);
        if (!response.IsSuccessStatusCode)
        {
            return new VerificationStep(TunnelStep, false, $"The address answered with HTTP {(int)response.StatusCode}. Is ngrok running and forwarding to port {edge.McpPort}?");
        }

        var body = await response.Content.ReadFromJsonAsync<InstanceBody>(ct);
        return body?.Instance is not null && body.Instance == expectedInstance
            ? new VerificationStep(TunnelStep, true, "The tunnel reaches this Waymark.")
            : new VerificationStep(TunnelStep, false, $"The address answered, but not from this Waymark. Check that ngrok forwards to port {edge.McpPort} on this computer.");
    }

    private async Task<VerificationStep> ProbeMetadataAsync(string origin, CancellationToken ct)
    {
        using var client = httpClients.CreateClient(HttpClientName);
        using var response = await client.GetAsync($"{origin}/.well-known/oauth-protected-resource", ct);
        if (!response.IsSuccessStatusCode)
        {
            return new VerificationStep(MetadataStep, false, $"The OAuth metadata answered with HTTP {(int)response.StatusCode}.");
        }

        var body = await response.Content.ReadFromJsonAsync<ResourceBody>(ct);
        var matches = body is not null
            && body.Resource == origin + "/mcp"
            && body.AuthorizationServers is { Count: > 0 } servers
            && servers[0] == origin;
        return matches
            ? new VerificationStep(MetadataStep, true, $"Claude will be told to use {origin}/mcp.")
            : new VerificationStep(MetadataStep, false, "The OAuth metadata does not name this address. Try saving the address again.");
    }

    private static async Task<VerificationStep> GuardAsync(string name, Func<Task<VerificationStep>> probe, CancellationToken ct)
    {
        try
        {
            return await probe();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new VerificationStep(name, false, "No answer within 5 seconds. Is ngrok running?");
        }
        catch (HttpRequestException ex)
        {
            return new VerificationStep(name, false, Describe(ex));
        }
        catch (JsonException)
        {
            return new VerificationStep(name, false, "The address answered, but not with what Waymark sends. Is ngrok forwarding to the right port?");
        }
    }

    private static string Describe(HttpRequestException ex) => ex.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "The name could not be resolved. Check the domain for typos.",
        HttpRequestError.SecureConnectionError => "The HTTPS certificate could not be validated.",
        HttpRequestError.ConnectionError => "Could not connect. Is ngrok running?",
        _ => ex.Message
    };

    private async Task<string?> InstanceIdAsync(CancellationToken ct)
    {
        var setting = await config.GetAsync(SettingScope.Global, SettingKeys.InstanceId, ct);
        return setting is null ? null : JsonSerializer.Deserialize<string>(setting.ValueJson);
    }

    private sealed record InstanceBody([property: JsonPropertyName("instance")] string? Instance);

    private sealed record ResourceBody(
        [property: JsonPropertyName("resource")] string? Resource,
        [property: JsonPropertyName("authorization_servers")] List<string>? AuthorizationServers);
}
