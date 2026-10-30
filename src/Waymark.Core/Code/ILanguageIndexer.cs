namespace Waymark.Core.Code;

public enum SyntaxIndexOutcome
{
    /// <summary>The file was parsed; <see cref="SyntaxIndexResult.File"/> is set.</summary>
    Indexed,

    /// <summary>The content hash equals the known hash, so nothing was parsed.</summary>
    Unchanged,

    /// <summary>The file is bigger than <see cref="CodeIndexLimits.MaxFileBytes"/>.</summary>
    TooLarge,

    /// <summary>The file looks binary and cannot be read as text.</summary>
    NotText,

    /// <summary>The indexer cannot run on this computer (a native library did not load). Files of its language stay out of the index; other languages are not affected.</summary>
    Unavailable
}

/// <param name="Outcome">What happened to the file.</param>
/// <param name="File">The parsed file; null unless <paramref name="Outcome"/> is <see cref="SyntaxIndexOutcome.Indexed"/>.</param>
/// <param name="Truncated">True when the file hit the per-file member or call-site limit and the rest was left out.</param>
public sealed record SyntaxIndexResult(SyntaxIndexOutcome Outcome, CodeFileIndex? File, bool Truncated);

/// <summary>The limits every indexer keeps to. Set by the code module from <c>Waymark:Code:*</c>.</summary>
public sealed record CodeIndexLimits(int MaxFileBytes, int MaxMembersPerFile, int MaxCallSitesPerFile);

/// <summary>
/// Reads one source file of one language into the shape the code index stores. A language is added by writing an indexer and registering it;
/// the walker, the watcher, the readers and the stores look an indexer up by file extension and know nothing else about the language.
/// </summary>
public interface ILanguageIndexer
{
    CodeLanguage Language { get; }

    /// <summary>The extensions this indexer reads, each with its dot and compared ignoring case, for example <c>.py</c>.</summary>
    IReadOnlySet<string> Extensions { get; }

    /// <summary>
    /// Reads one file and extracts its types, members and call sites (syntax only, no execution of the code). The file is read without blocking
    /// editors that keep it open. Use the same form of <paramref name="relativePath"/> on every call.
    /// </summary>
    /// <param name="absolutePath">A path already approved by the path guard.</param>
    /// <param name="relativePath">The workspace-relative path stored in the index.</param>
    /// <param name="knownHash">The hash of the indexed version, if any; when the file still has it nothing is parsed.</param>
    /// <exception cref="IOException">The file cannot be read (missing, locked exclusively, removed while reading).</exception>
    Task<SyntaxIndexResult> IndexFileAsync(string absolutePath, string relativePath, string? knownHash, CancellationToken ct);

    /// <summary>
    /// Like the overload above, with the modified time the caller already read (a directory walk has it), so the file is not asked for it twice.
    /// The default ignores it. Null means "read it from the file".
    /// </summary>
    Task<SyntaxIndexResult> IndexFileAsync(string absolutePath, string relativePath, string? knownHash, DateTimeOffset? modifiedUtc, CancellationToken ct) =>
        IndexFileAsync(absolutePath, relativePath, knownHash, ct);
}
