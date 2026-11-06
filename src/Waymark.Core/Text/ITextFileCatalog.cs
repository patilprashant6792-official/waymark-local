using Waymark.Core.Workspaces;

namespace Waymark.Core.Text;

/// <summary>A text file a tool may read and change. RelativePath uses '/'.</summary>
public sealed record CatalogFile(string RelativePath, string AbsolutePath, long Size);

/// <summary>A named path that was asked for but cannot be used, and why.</summary>
public sealed record CatalogRefusal(string RelativePath, string Reason);

/// <summary>
/// The files found, in path order, and what was left out on the way. Truncated is true when the walk stopped at its file limit,
/// so the listing is not the whole scope. Refused is filled only for named paths.
/// </summary>
public sealed record CatalogListing(
    IReadOnlyList<CatalogFile> Files,
    int TooLarge,
    int VendoredOrMinified,
    int Unreadable,
    bool Truncated,
    IReadOnlyList<CatalogRefusal> Refused);

/// <summary>
/// Lists the readable text files of a workspace with the same rules code_grep applies: the workspace's access rules, no vendored or
/// minified files, nothing over the size limit. The files module uses it for file_replace; the code module implements it.
/// </summary>
public interface ITextFileCatalog
{
    /// <summary>False when no implementation is registered (the code module is not loaded).</summary>
    bool IsAvailable { get; }

    /// <summary>Files over this size are left out.</summary>
    int MaxFileBytes { get; }

    /// <summary>Every allowed file of the workspace that matches the glob (all files when the glob is null).</summary>
    /// <exception cref="InvalidOperationException">The workspace folder does not exist, or no catalog is available.</exception>
    Task<CatalogListing> ListAsync(Workspace workspace, PathGlob? glob, CancellationToken ct);

    /// <summary>The named files, each checked with the same rules; a file that fails a rule is listed in Refused with the reason.</summary>
    /// <exception cref="InvalidOperationException">The workspace folder does not exist, or no catalog is available.</exception>
    Task<CatalogListing> ListPathsAsync(Workspace workspace, IReadOnlyList<string> relativePaths, CancellationToken ct);

    /// <summary>True for machine-written text (minified script or style, data on one line), judged by size and line count.</summary>
    bool LooksMinified(long sizeBytes, int lineCount);
}

/// <summary>The catalog of a host without the code module: it lists nothing and says so.</summary>
public sealed class NoTextFileCatalog : ITextFileCatalog
{
    private const string Unavailable = "Listing files needs the code module, which is not loaded.";

    public bool IsAvailable => false;

    public int MaxFileBytes => 0;

    public Task<CatalogListing> ListAsync(Workspace workspace, PathGlob? glob, CancellationToken ct) =>
        throw new InvalidOperationException(Unavailable);

    public Task<CatalogListing> ListPathsAsync(Workspace workspace, IReadOnlyList<string> relativePaths, CancellationToken ct) =>
        throw new InvalidOperationException(Unavailable);

    public bool LooksMinified(long sizeBytes, int lineCount) => false;
}
