namespace Waymark.Modules.Code.Content;

/// <summary>Searches the text of every readable file of a workspace by reading the files from disk.</summary>
public interface ITextSearchService
{
    /// <param name="workspace">A workspace name, or '*' for all enabled workspaces.</param>
    /// <param name="query">Literal text to find on a single line.</param>
    /// <param name="caseSensitive">False matches any letter case.</param>
    /// <param name="pathGlob">Optional filter on workspace-relative paths, e.g. '**/*.json'.</param>
    /// <param name="pageSize">Matching lines per page; clamped to the configured maximum.</param>
    /// <param name="page">1-based page number.</param>
    /// <exception cref="ArgumentException">The query, glob or workspace is invalid.</exception>
    Task<string> GrepAsync(string? workspace, string? query, bool caseSensitive, string? pathGlob, int? pageSize, int? page, CancellationToken ct);
}
