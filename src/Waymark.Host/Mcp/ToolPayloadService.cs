using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Settings;
using Waymark.Logging;

namespace Waymark.Host.Mcp;

/// <summary>What the owner chose about keeping the input and output of tool calls.</summary>
public sealed record PayloadSettings(bool Enabled, int Days);

/// <summary>
/// Keeps the input and output of tool calls for their details page. The input is what the AI sent (every text value passes the secret hiding, and a value
/// under a name like password or token is replaced outright); the output is what the AI received. Both are cut to a limit and flagged when cut. The owner
/// switches this on or off, chooses how many days it is kept, and can clear it. Nothing leaves this computer.
/// </summary>
public sealed class ToolPayloadService(IConfigStore config, IAuditStore audit, ToolResultGuard guard, TimeProvider time, ILogger<ToolPayloadService> logger)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions OutputOptions = new() { WriteIndented = true };

    private readonly Lock _gate = new();
    private PayloadSettings? _cached;
    private DateTimeOffset _cachedAt;

    public async Task<PayloadSettings> GetSettingsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_cached is not null && time.GetUtcNow() - _cachedAt < CacheFor)
            {
                return _cached;
            }
        }

        var enabled = await ReadAsync(SettingKeys.AuditPayloadsEnabled, true, value => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null, ct);
        var days = await ReadAsync(SettingKeys.AuditPayloadDays, ToolPayloadLimits.DefaultDays, value => value.TryGetInt32(out var number) ? number : null, ct);
        var settings = new PayloadSettings(enabled, Math.Clamp(days, 1, ToolPayloadLimits.DayChoices[^1]));
        lock (_gate)
        {
            _cached = settings;
            _cachedAt = time.GetUtcNow();
        }

        return settings;
    }

    public async Task SaveSettingsAsync(bool enabled, int days, CancellationToken ct)
    {
        if (!ToolPayloadLimits.DayChoices.Contains(days))
        {
            throw new ArgumentOutOfRangeException(nameof(days), "Choose one of the offered periods.");
        }

        var before = await GetSettingsAsync(ct);
        await config.SetAsync(SettingScope.Global, SettingKeys.AuditPayloadsEnabled, enabled ? "true" : "false", AuthDefaults.AdminActor, ct);
        await config.SetAsync(SettingScope.Global, SettingKeys.AuditPayloadDays, days.ToString(System.Globalization.CultureInfo.InvariantCulture), AuthDefaults.AdminActor, ct);
        lock (_gate)
        {
            _cached = new PayloadSettings(enabled, days);
            _cachedAt = time.GetUtcNow();
        }

        if (before.Enabled != enabled || before.Days != days)
        {
            await audit.AppendAsync(
                AuditEntries.Create(time, AuditCategory.Config, "config.audit_payloads", AuthDefaults.AdminActor, SettingKeys.AuditPayloadsEnabled, args: enabled ? $"on, {days} days" : "off"),
                ct);
        }
    }

    /// <summary>Deletes every kept input and output. The audit rows stay.</summary>
    public async Task<int> ClearAsync(CancellationToken ct)
    {
        var removed = await audit.ClearPayloadsAsync(ct);
        await audit.AppendAsync(
            AuditEntries.Create(time, AuditCategory.Config, "config.audit_payloads_cleared", AuthDefaults.AdminActor, SettingKeys.AuditPayloadsEnabled, args: removed.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ct);
        return removed;
    }

    /// <summary>Removes the kept inputs and outputs older than the chosen period. Called by the retention service.</summary>
    public async Task<int> PruneAsync(CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        return await audit.PrunePayloadsAsync(time.GetUtcNow() - TimeSpan.FromDays(settings.Days), ct);
    }

    /// <summary>
    /// The payload to keep for a call, or null when keeping is off. <paramref name="output"/> is the text the AI received. A failure here is the
    /// caller's to absorb: a call is never lost because its details could not be kept.
    /// </summary>
    public async Task<ToolPayload?> BuildAsync(IDictionary<string, JsonElement>? arguments, string output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        var settings = await GetSettingsAsync(ct);
        if (!settings.Enabled)
        {
            return null;
        }

        var input = arguments is null || arguments.Count == 0 ? "{}" : await CleanInputAsync(arguments, ct);
        var (inputText, inputCut) = Cut(input, ToolPayloadLimits.MaxInputChars);
        var (outputText, outputCut) = Cut(output, ToolPayloadLimits.MaxOutputChars);
        return new ToolPayload(inputText, inputCut, input.Length, outputText, outputCut, output.Length);
    }

    private async Task<string> CleanInputAsync(IDictionary<string, JsonElement> arguments, CancellationToken ct)
    {
        var clean = new JsonObject();
        foreach (var pair in arguments.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            clean[pair.Key] = LogScrubber.IsSensitiveKey(pair.Key)
                ? JsonValue.Create(LogScrubber.Hidden)
                : await CleanAsync(JsonNode.Parse(pair.Value.GetRawText()), ct);
        }

        return clean.ToJsonString(OutputOptions);
    }

    // Every text value goes through the same hiding as a result; a nested property with a sensitive name is replaced as a whole.
    private async Task<JsonNode?> CleanAsync(JsonNode? node, CancellationToken ct)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(await guard.CleanTextAsync(text, ct));
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var pair in obj.ToList())
                {
                    copy[pair.Key] = LogScrubber.IsSensitiveKey(pair.Key) ? JsonValue.Create(LogScrubber.Hidden) : await CleanAsync(pair.Value?.DeepClone(), ct);
                }

                return copy;
            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array.ToList())
                {
                    items.Add(await CleanAsync(item?.DeepClone(), ct));
                }

                return items;
            default:
                return node?.DeepClone();
        }
    }

    private static (string Text, bool Cut) Cut(string text, int max)
    {
        if (text.Length <= max)
        {
            return (text, false);
        }

        var cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return (text[..cut], true);
    }

    private async Task<T> ReadAsync<T>(string key, T fallback, Func<JsonElement, T?> parse, CancellationToken ct)
        where T : struct
    {
        try
        {
            var setting = await config.GetAsync(SettingScope.Global, key, ct);
            if (setting is null)
            {
                return fallback;
            }

            using var document = JsonDocument.Parse(setting.ValueJson);
            return parse(document.RootElement) ?? fallback;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex, "The setting {Key} could not be read; the default is used", key);
            return fallback;
        }
    }
}
