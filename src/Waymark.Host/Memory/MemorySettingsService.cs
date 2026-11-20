using System.Globalization;
using System.Text.Json;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Settings;

namespace Waymark.Host.Memory;

/// <summary>
/// The search-by-meaning settings: the switch (off by default), the active provider, model and dimension, the per-provider keys and the per-workspace exclusion.
/// Every change publishes <see cref="MemorySettingsChanged"/>, which the index listens to.
/// </summary>
public sealed class MemorySettingsService(IConfigStore config, MemoryKeyStore keys, IEmbeddingProviders providers, IEventBus events) : IMemorySettings
{
    /// <summary>The provider chosen until the owner picks one.</summary>
    public const string DefaultProvider = "voyage";

    private const int MaxModelChars = 120;

    public async Task<MemorySettings> GetAsync(CancellationToken ct)
    {
        var enabled = await ReadAsync(SettingScope.Global, SettingKeys.MemorySemanticEnabled, false, ct);
        var provider = await ReadAsync(SettingScope.Global, SettingKeys.MemoryProvider, DefaultProvider, ct);
        var known = providers.Find(provider) ?? providers.Find(DefaultProvider) ?? providers.All[0];
        var model = await ReadAsync(SettingScope.Global, SettingKeys.MemoryModel, string.Empty, ct);
        var dimension = await ReadAsync(SettingScope.Global, SettingKeys.MemoryDimension, MemorySettings.DefaultDimension, ct);
        var hasKey = await keys.GetAsync(known.Id, ct) is not null;
        return new MemorySettings(
            enabled,
            known.Id,
            string.IsNullOrWhiteSpace(model) ? known.DefaultModel : model,
            Math.Clamp(dimension, MemorySettings.MinDimension, MemorySettings.MaxDimension),
            hasKey);
    }

    public Task<string?> GetKeyAsync(string provider, CancellationToken ct) => keys.GetAsync(Known(provider).Id, ct);

    public Task<MemoryKeyInfo> GetKeyInfoAsync(string provider, CancellationToken ct) => keys.GetInfoAsync(Known(provider).Id, ct);

    public Task<bool> IsExcludedAsync(long workspaceId, CancellationToken ct) =>
        ReadAsync(WorkspaceScope(workspaceId), SettingKeys.MemorySemanticExcluded, false, ct);

    public async Task SetEnabledAsync(bool enabled, string changedBy, CancellationToken ct)
    {
        await config.SetAsync(SettingScope.Global, SettingKeys.MemorySemanticEnabled, JsonSerializer.Serialize(enabled), changedBy, ct);
        await events.PublishAsync(new MemorySettingsChanged());
    }

    public async Task SetModelAsync(string provider, string model, int dimension, string changedBy, CancellationToken ct)
    {
        var known = Known(provider);
        var cleanModel = (model ?? string.Empty).Trim();
        if (cleanModel.Length == 0 || cleanModel.Length > MaxModelChars || cleanModel.Any(c => char.IsWhiteSpace(c) || c is '/' or '?' or '#' or '\\'))
        {
            throw new ArgumentException($"The model is the provider's model name, such as '{known.DefaultModel}', without spaces or slashes.", nameof(model));
        }

        if (dimension < MemorySettings.MinDimension || dimension > MemorySettings.MaxDimension)
        {
            throw new ArgumentException($"The dimension is a number from {MemorySettings.MinDimension} to {MemorySettings.MaxDimension}.", nameof(dimension));
        }

        await config.SetAsync(SettingScope.Global, SettingKeys.MemoryProvider, JsonSerializer.Serialize(known.Id), changedBy, ct);
        await config.SetAsync(SettingScope.Global, SettingKeys.MemoryModel, JsonSerializer.Serialize(cleanModel), changedBy, ct);
        await config.SetAsync(SettingScope.Global, SettingKeys.MemoryDimension, JsonSerializer.Serialize(dimension), changedBy, ct);
        await events.PublishAsync(new MemorySettingsChanged());
    }

    public async Task SaveKeyAsync(string provider, string key, string changedBy, CancellationToken ct)
    {
        await keys.SetAsync(Known(provider).Id, key, changedBy, ct);
        await events.PublishAsync(new MemorySettingsChanged());
    }

    public async Task ClearKeyAsync(string provider, string changedBy, CancellationToken ct)
    {
        await keys.ClearAsync(Known(provider).Id, changedBy, ct);
        await events.PublishAsync(new MemorySettingsChanged());
    }

    public async Task SetExcludedAsync(long workspaceId, bool excluded, string changedBy, CancellationToken ct)
    {
        await config.SetAsync(WorkspaceScope(workspaceId), SettingKeys.MemorySemanticExcluded, JsonSerializer.Serialize(excluded), changedBy, ct);
        await events.PublishAsync(new MemorySettingsChanged());
    }

    private IEmbeddingProvider Known(string provider) =>
        providers.Find(provider) ?? throw new ArgumentException($"Unknown provider '{provider}'. Choose one of: {string.Join(", ", providers.All.Select(p => p.Id))}.", nameof(provider));

    private static SettingScope WorkspaceScope(long workspaceId) =>
        new(SettingScopeKind.Workspace, workspaceId.ToString(CultureInfo.InvariantCulture));

    // A setting that cannot be read as its type counts as unset: the default keeps search by meaning off rather than failing a call.
    private async Task<T> ReadAsync<T>(SettingScope scope, string key, T fallback, CancellationToken ct)
    {
        var setting = await config.GetAsync(scope, key, ct);
        if (setting is null)
        {
            return fallback;
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(setting.ValueJson);
            return value is null ? fallback : value;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}
