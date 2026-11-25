namespace Waymark.Core.Memory;

public enum MemoryHitKind
{
    Note,
    Flow,
}

/// <summary>Which legs of the search found an item.</summary>
public enum MemoryMatch
{
    Words,
    Meaning,
    Both,
}

/// <summary>
/// One result of a memory search: a flow or a note. <paramref name="FlowId"/> is the flow a note belongs to (null for a workspace note);
/// <paramref name="Steps"/> are the steps of a flow that matched or whose notes matched, in flow order; <paramref name="Notes"/> are matching notes grouped under their flow.
/// <paramref name="Score"/> is the fused rank score that orders the results; it has no meaning on its own.
/// <paramref name="Similarity"/> is how close the item is in meaning to the query (the best of a flow and the notes grouped under it), or null when search by
/// meaning did not find it. It is the match score agents see; it never changes the order.
/// </summary>
public sealed record MemoryHit(
    MemoryHitKind Kind,
    long Id,
    long? FlowId,
    string? StepSpec,
    double Score,
    MemoryMatch MatchedBy,
    IReadOnlyList<string> Steps,
    IReadOnlyList<long> Notes,
    double? Similarity = null);

/// <param name="UsedMeaning">True when the meaning legs ran (switch on, key there, provider answered).</param>
/// <param name="Notice">Why only words were used when search by meaning is on but failed; null otherwise.</param>
/// <param name="Total">How many hits the query has in all, across pages (0 when the search does not say).</param>
/// <param name="Skip">How many hits came before this page.</param>
public sealed record MemorySearchResult(IReadOnlyList<MemoryHit> Hits, bool UsedMeaning, string? Notice, int Total = 0, int Skip = 0)
{
    /// <summary>Search by meaning is switched on for the workspace, whether or not it answered this query. The answer is then the short index form.</summary>
    public bool MeaningOn => UsedMeaning || Notice is not null;

    /// <summary>True when more hits follow this page.</summary>
    public bool HasMore => Total > Skip + Hits.Count;
}

/// <summary>
/// Hybrid search over the flows and notes of one workspace: words always, meaning when the owner switched it on. Never throws for a provider failure:
/// it answers with words and says why in <see cref="MemorySearchResult.Notice"/>.
/// </summary>
public interface IMemorySearch
{
    Task<MemorySearchResult> SearchAsync(long workspaceId, string query, int take, CancellationToken ct);

    /// <summary>One page of the hits: <paramref name="skip"/> hits are left out, then at most <paramref name="take"/> come.</summary>
    Task<MemorySearchResult> SearchAsync(long workspaceId, string query, int skip, int take, CancellationToken ct) => SearchAsync(workspaceId, query, take, ct);
}
