namespace Waymark.Modules.Files;

/// <summary>Creates, moves and deletes files and folders inside a workspace. Every path goes through the path guard.</summary>
public interface IFileManager
{
    /// <summary>Creates the folders, nested paths included. A folder that exists already counts as success.</summary>
    /// <exception cref="ArgumentException">No paths, too many paths, or the workspace name is empty or '*'.</exception>
    /// <exception cref="KeyNotFoundException">The workspace does not exist.</exception>
    /// <exception cref="InvalidOperationException">The workspace is disabled.</exception>
    Task<IReadOnlyList<FileOperationResult>> CreateFoldersAsync(string? workspace, IReadOnlyList<string?> folders, CancellationToken ct);

    /// <summary>All moves are validated before any file is moved; one invalid entry means nothing is moved.</summary>
    /// <exception cref="ArgumentException">No entries, too many entries, or the workspace name is empty or '*'.</exception>
    /// <exception cref="KeyNotFoundException">The workspace does not exist.</exception>
    /// <exception cref="InvalidOperationException">The workspace is disabled.</exception>
    Task<IReadOnlyList<FileOperationResult>> MoveFilesAsync(string? workspace, IReadOnlyList<MoveEntry> moves, CancellationToken ct);

    /// <summary>Deletes the files one by one; a file that cannot be deleted fails on its own.</summary>
    /// <exception cref="ArgumentException">No paths, too many paths, or the workspace name is empty or '*'.</exception>
    /// <exception cref="KeyNotFoundException">The workspace does not exist.</exception>
    /// <exception cref="InvalidOperationException">The workspace is disabled.</exception>
    Task<IReadOnlyList<FileOperationResult>> DeleteFilesAsync(string? workspace, IReadOnlyList<string?> files, CancellationToken ct);

    /// <summary>Moves or renames one folder with its contents. A refused move comes back as a failed result.</summary>
    /// <exception cref="ArgumentException">The workspace name is empty or '*'.</exception>
    /// <exception cref="KeyNotFoundException">The workspace does not exist.</exception>
    /// <exception cref="InvalidOperationException">The workspace is disabled.</exception>
    Task<FileOperationResult> MoveFolderAsync(string? workspace, string? from, string? to, CancellationToken ct);

    /// <summary>Deletes the folders with their contents, deepest first. A folder that is already gone counts as success.</summary>
    /// <exception cref="ArgumentException">No paths, too many paths, or the workspace name is empty or '*'.</exception>
    /// <exception cref="KeyNotFoundException">The workspace does not exist.</exception>
    /// <exception cref="InvalidOperationException">The workspace is disabled.</exception>
    Task<IReadOnlyList<FileOperationResult>> DeleteFoldersAsync(string? workspace, IReadOnlyList<string?> folders, CancellationToken ct);
}
