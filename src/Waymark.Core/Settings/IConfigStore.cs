namespace Waymark.Core.Settings;

public interface IConfigStore
{
    Task<SettingValue?> GetAsync(SettingScope scope, string key, CancellationToken ct);

    /// <summary>Creates or updates a setting. An unchanged value returns the current setting and writes no history.</summary>
    Task<SettingValue> SetAsync(SettingScope scope, string key, string valueJson, string changedBy, CancellationToken ct);

    /// <summary>Creates the setting only when it is absent. True when this call created it.</summary>
    Task<bool> TryAddAsync(SettingScope scope, string key, string valueJson, string changedBy, CancellationToken ct);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<SettingHistoryEntry>> GetHistoryAsync(SettingScope scope, string key, int limit, CancellationToken ct);
}
