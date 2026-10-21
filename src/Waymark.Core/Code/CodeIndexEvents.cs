namespace Waymark.Core.Code;

public enum CodeIndexPhase
{
    Started,
    Running,
    Completed,
    Failed,
}

/// <summary>Progress of a whole-workspace index run (start-up reconcile, re-index, folder change). Single-file updates are not reported.</summary>
/// <param name="FilesTotal">Number of C# files found on disk; 0 until the folder walk is done.</param>
/// <param name="FilesProcessed">Files looked at so far, whether or not they needed parsing.</param>
/// <param name="FilesChanged">Files that were parsed and stored.</param>
/// <param name="FilesFailed">Files that could not be read and were left as they were.</param>
public sealed record CodeIndexProgress(
    long WorkspaceId,
    CodeIndexPhase Phase,
    int FilesTotal,
    int FilesProcessed,
    int FilesChanged,
    int FilesFailed);
