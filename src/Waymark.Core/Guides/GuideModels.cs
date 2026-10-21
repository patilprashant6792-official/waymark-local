namespace Waymark.Core.Guides;

/// <summary>
/// A guide as the call pipeline and the admin page see it. <paramref name="Content"/> is what agents receive; <paramref name="Hash"/> identifies
/// the part of it that only changes when Waymark or the owner's text changes (the live setup block of the overview is not part of the hash).
/// </summary>
public sealed record ToolGuide(string Module, string Content, string Hash);

/// <summary>The owner's text for the overview, as saved. It is kept under the module key <see cref="HostPromptRules.StoreKey"/>.</summary>
public sealed record GuideOverride(string Module, string Content, DateTimeOffset ChangedUtc, string ChangedBy);

/// <summary>The guide text a client last received for a module, identified by its hash.</summary>
public sealed record GuideDelivery(string ClientId, string Module, string ContentHash, DateTimeOffset DeliveredUtc);
