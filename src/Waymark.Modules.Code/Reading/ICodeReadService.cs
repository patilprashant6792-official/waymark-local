namespace Waymark.Modules.Code.Reading;

/// <summary>Reads code on behalf of code_outline, code_method and code_read. Every path goes through the path guard.</summary>
public interface ICodeReadService
{
    /// <summary>Structure of one or more C# files (comma separated paths), parsed from the disk so it is never stale.</summary>
    /// <exception cref="ArgumentException">No path, too many paths, or no file could be analyzed.</exception>
    Task<string> OutlineAsync(string? workspace, string? relativeFilePaths, bool includePrivateMembers, CancellationToken ct);

    /// <summary>Source of one or more members (comma separated names) of a C# file, with line numbers.</summary>
    /// <exception cref="ArgumentException">Missing or invalid input, or the file is not a readable C# file.</exception>
    Task<string> MethodsAsync(string? workspace, string? relativeFilePath, string? methodNames, string? className, CancellationToken ct);

    /// <summary>Numbered lines of a text file: whole, a line range, or the lines containing a text.</summary>
    /// <exception cref="ArgumentException">Missing or invalid input, or the file is not readable text.</exception>
    Task<string> ReadAsync(string? workspace, string? relativeFilePath, int? startLine, int? endLine, string? query, CancellationToken ct);
}
