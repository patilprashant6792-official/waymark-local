using Waymark.Core.Security;

namespace Waymark.Core.Transfer;

/// <summary>Which kinds of content a bundle export carries or an import applies.</summary>
[Flags]
public enum BundleContent
{
    None = 0,
    AccessRules = 1,
    FeatureMaps = 2,
    All = AccessRules | FeatureMaps,
}

/// <summary>One access rule as it travels. The workspace is the one that holds it, so no id of this computer is written.</summary>
public sealed record BundleRule(AccessRuleKind Kind, string Pattern, string Note, bool Enabled, DateTimeOffset? CreatedUtc);

/// <summary>
/// One step of a feature map, in its canonical text form. Depth and Link are what makes a map a flow; a bundle of an earlier version has neither, and reads as depth 0 with a direct call.
/// </summary>
public sealed record BundleComponent(string Spec, string Note, int Depth = 0, string Link = "call");

/// <summary>
/// One feature map: its name, summary and steps in the order they were saved. Names and paths only, never code.
/// Ordered is false for a map that is a plain list of components (saved before flows existed, or exported by an earlier version).
/// </summary>
public sealed record BundleMap(string Name, string Summary, DateTimeOffset? UpdatedUtc, IReadOnlyList<BundleComponent> Components, bool Ordered = false);

/// <summary>
/// A workspace as it travels. <paramref name="PathHint"/> is the folder it had on the computer that exported it: only a suggestion, because the folder is chosen again on the computer that imports.
/// </summary>
public sealed record BundleWorkspace(string Name, string? PathHint, bool Enabled, IReadOnlyList<BundleRule> AccessRules, IReadOnlyList<BundleMap> FeatureMaps);

/// <summary>The file the owner moves to another computer: the workspaces with their access rules and feature maps. Nothing that identifies an installation, nothing secret.</summary>
public sealed record WorkspaceBundle(string Format, int FormatVersion, DateTimeOffset ExportedUtc, IReadOnlyList<BundleWorkspace> Workspaces);
