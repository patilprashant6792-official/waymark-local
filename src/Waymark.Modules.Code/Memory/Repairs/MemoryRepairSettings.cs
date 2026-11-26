using System.Globalization;
using System.Text.Json;
using Waymark.Core.Memory;
using Waymark.Core.Settings;

namespace Waymark.Modules.Code.Memory.Repairs;

/// <summary>
/// The owner's repair settings as three global settings: the mode (automatic by default), automatic fixes (on by default) and the per-session budget.
/// Read on most tool calls (the repair notice), so the values are kept for a few seconds; saving forgets them at once. An unreadable value falls back to its default.
/// </summary>
internal sealed class MemoryRepairSettings(IConfigStore config, TimeProvider time) : IMemoryRepairSettings
{
    internal const string ModeKey = "memory.repairs.mode";
    internal const string AutoFixKey = "memory.repairs.auto_fix";
    internal const string BudgetKey = "memory.repairs.budget";

    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private (MemoryRepairOptions Options, DateTimeOffset At)? _cached;

    public async Task<MemoryRepairOptions> GetAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_cached is { } hit && now - hit.At < CacheTime)
            {
                return hit.Options;
            }
        }

        var defaults = MemoryRepairOptions.Default;
        var mode = await ReadAsync(ModeKey, element => element.ValueKind == JsonValueKind.String ? MemoryRepairOptions.ModeFrom(element.GetString()) : defaults.Mode, defaults.Mode, ct).ConfigureAwait(false);
        var autoFix = await ReadAsync(AutoFixKey, element => element.ValueKind is JsonValueKind.True or JsonValueKind.False ? element.GetBoolean() : defaults.AutoFix, defaults.AutoFix, ct).ConfigureAwait(false);
        var budget = await ReadAsync(BudgetKey, element => element.TryGetInt32(out var value) ? value : defaults.Budget, defaults.Budget, ct).ConfigureAwait(false);
        var options = new MemoryRepairOptions(mode, autoFix, Math.Clamp(budget, 1, MemoryRepairOptions.MaxBudget));
        lock (_gate)
        {
            _cached = (options, now);
        }

        return options;
    }

    public async Task SetAsync(MemoryRepairOptions options, string changedBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(changedBy);
        await config.SetAsync(SettingScope.Global, ModeKey, JsonSerializer.Serialize(MemoryRepairOptions.ModeText(options.Mode)), changedBy, ct).ConfigureAwait(false);
        await config.SetAsync(SettingScope.Global, AutoFixKey, options.AutoFix ? "true" : "false", changedBy, ct).ConfigureAwait(false);
        await config.SetAsync(
            SettingScope.Global,
            BudgetKey,
            Math.Clamp(options.Budget, 1, MemoryRepairOptions.MaxBudget).ToString(CultureInfo.InvariantCulture),
            changedBy,
            ct).ConfigureAwait(false);
        lock (_gate)
        {
            _cached = null;
        }
    }

    private async Task<T> ReadAsync<T>(string key, Func<JsonElement, T> parse, T fallback, CancellationToken ct)
    {
        try
        {
            var setting = await config.GetAsync(SettingScope.Global, key, ct).ConfigureAwait(false);
            if (setting is null)
            {
                return fallback;
            }

            using var document = JsonDocument.Parse(setting.ValueJson);
            return parse(document.RootElement);
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}
