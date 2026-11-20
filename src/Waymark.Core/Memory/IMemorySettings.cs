namespace Waymark.Core.Memory;

/// <summary>
/// The search-by-meaning settings of this install. <paramref name="Enabled"/> is the owner's switch (off by default); <paramref name="HasKey"/> says whether the active provider has a key saved.
/// The index embeds only when both are true.
/// </summary>
public sealed record MemorySettings(bool Enabled, string Provider, string Model, int Dimension, bool HasKey)
{
    public const int DefaultDimension = 1024;
    public const int MinDimension = 64;
    public const int MaxDimension = 4096;

    /// <summary>The id stored next to every vector: '&lt;provider&gt;/&lt;model&gt;@&lt;dimension&gt;'. Vectors of different ids are never compared.</summary>
    public string ModelId => $"{Provider}/{Model}@{Dimension}";

    /// <summary>On, and a key is there to use.</summary>
    public bool IsActive => Enabled && HasKey;
}

/// <summary>A saved key as the Setup page shows it: never the key, only that it exists and its last four characters.</summary>
public sealed record MemoryKeyInfo(string Provider, bool Saved, string? LastFour, bool Unreadable);

/// <summary>
/// Reads and writes the search-by-meaning settings: the switch, the provider and model, the dimension, the keys (encrypted per provider)
/// and the per-workspace exclusion. Every change publishes MemorySettingsChanged.
/// </summary>
public interface IMemorySettings
{
    Task<MemorySettings> GetAsync(CancellationToken ct);

    /// <summary>The key of the provider in plain text, or null when none is saved or it can no longer be read.</summary>
    Task<string?> GetKeyAsync(string provider, CancellationToken ct);

    Task<MemoryKeyInfo> GetKeyInfoAsync(string provider, CancellationToken ct);

    /// <summary>True when the owner excluded the workspace: nothing of it is sent and its vectors are deleted.</summary>
    Task<bool> IsExcludedAsync(long workspaceId, CancellationToken ct);

    Task SetEnabledAsync(bool enabled, string changedBy, CancellationToken ct);

    /// <summary>Chooses the active provider, model and dimension. A change of any of them means a new model id, so everything is embedded again.</summary>
    /// <exception cref="ArgumentException">Unknown provider, empty model, or a dimension out of range.</exception>
    Task SetModelAsync(string provider, string model, int dimension, string changedBy, CancellationToken ct);

    Task SaveKeyAsync(string provider, string key, string changedBy, CancellationToken ct);

    Task ClearKeyAsync(string provider, string changedBy, CancellationToken ct);

    Task SetExcludedAsync(long workspaceId, bool excluded, string changedBy, CancellationToken ct);
}
