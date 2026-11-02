namespace Waymark.Modules.Code.Memory;

/// <summary>
/// The feature maps behind the recall and memorise tools. Every path goes through the path guard, and every answer is text ready to show.
/// </summary>
/// <exception cref="ArgumentException">Missing or invalid input, or a component that is not in the code on this branch.</exception>
public interface IFeatureMemoryService
{
    /// <summary>The maps of a workspace, newest first, filtered by the words of the query and paged.</summary>
    Task<string> ListAsync(string? workspace, string? query, int? page, int? pageSize, CancellationToken ct);

    /// <summary>The components of the maps with these ids, read from the code as it is now. A component that is not here is listed, never hidden.</summary>
    Task<string> GetAsync(string? workspace, string? ids, int? maxTokens, CancellationToken ct);

    /// <summary>Creates the map or merges into it. Every added component must exist now; nothing is saved otherwise.</summary>
    Task<string> SaveAsync(string? workspace, string? name, string? summary, string? components, string? remove, CancellationToken ct);

    Task<string> DeleteAsync(string? workspace, string? name, CancellationToken ct);
}
