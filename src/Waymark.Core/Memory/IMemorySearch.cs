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
/// </summary>
public sealed record MemoryHit(
    MemoryHitKind Kind,
    long Id,
    long? FlowId,
    string? StepSpec,
    double Score,
    MemoryMatch MatchedBy,
    IReadOnlyList<string> Steps,
    IReadOnlyList<long> Notes);

/// <param name="UsedMeaning">True when the meaning legs ran (switch on, key there, provider answered).</param>
/// <param name="Notice">Why only words were used when search by meaning is on but failed; null otherwise.</param>
public sealed record MemorySearchResult(IReadOnlyList<MemoryHit> Hits, bool UsedMeaning, string? Notice);

/// <summary>
/// Hybrid search over the flows and notes of one workspace: words always, meaning when the owner switched it on. Never throws for a provider failure:
/// it answers with words and says why in <see cref="MemorySearchResult.Notice"/>.
/// </summary>
public interface IMemorySearch
{
    Task<MemorySearchResult> SearchAsync(long workspaceId, string query, int take, CancellationToken ct);
}
