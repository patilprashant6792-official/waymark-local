using Waymark.Core.Code;

namespace Waymark.Modules.Code;

public enum SyntaxIndexOutcome
{
    /// <summary>The file was parsed; <see cref="SyntaxIndexResult.File"/> is set.</summary>
    Indexed,

    /// <summary>The content hash equals the known hash, so nothing was parsed.</summary>
    Unchanged,

    /// <summary>The file is bigger than <see cref="CodeIndexOptions.MaxFileBytes"/>.</summary>
    TooLarge,

    /// <summary>The file looks binary and cannot be read as text.</summary>
    NotText
}

/// <param name="Outcome">What happened to the file.</param>
/// <param name="File">The parsed file; null unless <paramref name="Outcome"/> is <see cref="SyntaxIndexOutcome.Indexed"/>.</param>
/// <param name="Truncated">True when the file hit the per-file member or call-site limit and the rest was left out.</param>
public sealed record SyntaxIndexResult(SyntaxIndexOutcome Outcome, CodeFileIndex? File, bool Truncated);

public interface ISyntaxIndexer
{
    /// <summary>
    /// Reads one C# file and extracts its types, members and call sites with Roslyn (syntax only, no compilation).
    /// The file is read without blocking editors that keep it open. Use the same form of <paramref name="relativePath"/> on every call.
    /// </summary>
    /// <param name="absolutePath">A path already approved by the path guard.</param>
    /// <param name="relativePath">The workspace-relative path stored in the index.</param>
    /// <param name="knownHash">The hash of the indexed version, if any; when the file still has it nothing is parsed.</param>
    /// <exception cref="IOException">The file cannot be read (missing, locked exclusively, removed while reading).</exception>
    Task<SyntaxIndexResult> IndexFileAsync(string absolutePath, string relativePath, string? knownHash, CancellationToken ct);
}
