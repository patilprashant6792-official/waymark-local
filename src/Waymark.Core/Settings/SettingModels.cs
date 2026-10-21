namespace Waymark.Core.Settings;

public sealed record SettingValue(
    SettingScope Scope,
    string Key,
    string ValueJson,
    long Version,
    DateTimeOffset UpdatedUtc);

public sealed record SettingHistoryEntry(
    long Id,
    SettingScope Scope,
    string Key,
    string? OldJson,
    string NewJson,
    DateTimeOffset ChangedUtc,
    string ChangedBy);
