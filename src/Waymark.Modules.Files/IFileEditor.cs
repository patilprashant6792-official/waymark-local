namespace Waymark.Modules.Files;

/// <summary>Creates, replaces and edits files inside a workspace. Every path goes through the path guard.</summary>
public interface IFileEditor
{
    /// <summary>Writes the files in order. A file that cannot be written fails on its own; the others still run.</summary>
    /// <exception cref="ArgumentException">No files, too many files, or the workspace name is empty or '*'.</exception>
    /// <exception cref="KeyNotFoundException">The workspace does not exist.</exception>
    /// <exception cref="InvalidOperationException">The workspace is disabled.</exception>
    Task<IReadOnlyList<FileWriteResult>> WriteAsync(string? workspace, IReadOnlyList<WriteFileEntry> files, CancellationToken ct);

    /// <summary>
    /// Applies all patches to one file or none. Line numbers refer to the file as it was before the call.
    /// A refused or invalid edit comes back as a failed result and the file is untouched.
    /// </summary>
    /// <exception cref="ArgumentException">No patches, too many patches, or the workspace name is empty or '*'.</exception>
    /// <exception cref="KeyNotFoundException">The workspace does not exist.</exception>
    /// <exception cref="InvalidOperationException">The workspace is disabled.</exception>
    Task<FileWriteResult> EditLinesAsync(string? workspace, string? relativeFilePath, IReadOnlyList<PatchOperation> patches, CancellationToken ct);
}
