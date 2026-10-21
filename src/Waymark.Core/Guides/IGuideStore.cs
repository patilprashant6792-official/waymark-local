namespace Waymark.Core.Guides;

/// <summary>Keeps the owner's text for the overview (under the key <see cref="HostPromptRules.StoreKey"/>) and what each client has been sent.</summary>
public interface IGuideStore
{
    Task<IReadOnlyList<GuideOverride>> ListOverridesAsync(CancellationToken ct);

    /// <summary>Creates or replaces the owner's text stored under a key.</summary>
    Task SetOverrideAsync(string module, string content, string changedBy, DateTimeOffset now, CancellationToken ct);

    /// <summary>Removes the owner's text stored under a key. True when there was one.</summary>
    Task<bool> DeleteOverrideAsync(string module, CancellationToken ct);

    Task<IReadOnlyList<GuideDelivery>> ListDeliveriesAsync(CancellationToken ct);

    /// <summary>Remembers that the client now holds the guide with this hash.</summary>
    Task RecordDeliveryAsync(string clientId, string module, string contentHash, DateTimeOffset now, CancellationToken ct);
}
