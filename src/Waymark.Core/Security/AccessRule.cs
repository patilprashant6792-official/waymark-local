namespace Waymark.Core.Security;

/// <summary>What a workspace rule does. Rules only add to the built-in policy: they can block more, or allow one more file type.</summary>
public enum AccessRuleKind
{
    /// <summary>A folder name (may use * and ?) anywhere in the path, or a path from the workspace root such as src/Legacy.</summary>
    BlockFolder,

    /// <summary>A file name or pattern such as *.sql or notes-*.txt.</summary>
    BlockFileName,

    /// <summary>A word that, found anywhere in a path, blocks it.</summary>
    BlockKeyword,

    /// <summary>A file type (for example .proto) that tools may open in this workspace. The built-in blocks still win.</summary>
    AllowExtension,
}

/// <summary>One rule the owner saved for a workspace in the admin UI.</summary>
public sealed record AccessRule(
    long Id,
    long WorkspaceId,
    AccessRuleKind Kind,
    string Pattern,
    bool Enabled,
    string Note,
    DateTimeOffset CreatedUtc);
