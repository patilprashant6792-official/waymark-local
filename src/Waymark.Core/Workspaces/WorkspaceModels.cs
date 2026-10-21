namespace Waymark.Core.Workspaces;

/// <summary>What the folder scan found. File names are relative to the workspace folder.</summary>
public sealed record WorkspaceMetadata(
    IReadOnlyList<string> SolutionFiles,
    IReadOnlyList<string> ProjectFiles,
    string? DetectedFramework,
    bool ProjectListTruncated);

/// <summary>Result of checking a folder. NormalizedPath is the canonical full path and is set only when IsValid.</summary>
public sealed record WorkspaceValidation(bool IsValid, string? Error, string? NormalizedPath, WorkspaceMetadata? Metadata)
{
    public static WorkspaceValidation Invalid(string error) => new(false, error, null, null);

    public static WorkspaceValidation Valid(string normalizedPath, WorkspaceMetadata metadata) =>
        new(true, null, normalizedPath, metadata);
}

/// <summary>Result of adding a workspace. Workspace is set only when IsSuccess.</summary>
public sealed record WorkspaceAddResult(bool IsSuccess, string? Error, Workspace? Workspace, WorkspaceMetadata? Metadata)
{
    public static WorkspaceAddResult Failure(string error) => new(false, error, null, null);

    public static WorkspaceAddResult Success(Workspace workspace, WorkspaceMetadata metadata) =>
        new(true, null, workspace, metadata);
}

public enum WorkspaceChangeKind
{
    Added,
    Removed,
    Enabled,
    Disabled,
}
