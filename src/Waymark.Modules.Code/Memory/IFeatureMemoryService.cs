namespace Waymark.Modules.Code.Memory;

/// <summary>
/// The feature maps (flows) behind the recall and memorise tools. Every path goes through the path guard, and every answer is text ready to show.
/// </summary>
/// <exception cref="ArgumentException">Missing or invalid input, or a step that is not in the code on this branch.</exception>
public interface IFeatureMemoryService
{
    /// <summary>The flows of a workspace, newest first, filtered by the words of the query (a method name finds the flows it is a step of) and paged.</summary>
    Task<string> ListAsync(string? workspace, string? query, int? page, int? pageSize, CancellationToken ct);

    /// <summary>The steps of the flows with these ids, each with its place in the code as it is now and the status of its link. A step that is not here is listed, never hidden. No code.</summary>
    Task<string> GetAsync(string? workspace, string? ids, CancellationToken ct);

    /// <summary>
    /// Creates the flow (without <paramref name="expectedVersion"/>) or replaces all its steps (with the version that was read). New or changed steps must exist now and their links must hold;
    /// steps that were stored already stay whatever branch they came from.
    /// </summary>
    Task<string> SaveAsync(string? workspace, string? name, string? summary, string? steps, int? expectedVersion, string? newName, string? distinct, CancellationToken ct);

    Task<string> DeleteAsync(string? workspace, string? name, CancellationToken ct);
}
