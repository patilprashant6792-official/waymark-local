namespace Waymark.Core.Memory;

/// <summary>What drifted in a workspace's memory.</summary>
public enum MemoryRepairKind
{
    /// <summary>A flow says a step calls the next one, both are in the code, and the code makes no such call.</summary>
    LinkBroken,

    /// <summary>A flow step was last seen on the branch checked out now (or the folder is not git) and is no longer in the code.</summary>
    StepGone,

    /// <summary>Like <see cref="StepGone"/>, but a method of the same name (and type) is in another file: the step probably moved.</summary>
    StepMoved,

    /// <summary>A note anchored to code (a file, a member or a step) whose code changed since it was written or confirmed.</summary>
    NoteCodeChanged,

    /// <summary>A flow note whose flow was saved again since the note was written or confirmed.</summary>
    NoteFlowChanged,
}

public enum MemoryRepairTarget
{
    Flow,
    Note,
}

public enum MemoryRepairState
{
    /// <summary>Waiting for a repair.</summary>
    Open,

    /// <summary>Waymark repaired it itself (a step that moved, with one clear new place); kept so the owner can see and undo it.</summary>
    AutoFixed,

    /// <summary>A later check found the target healthy (repaired, deleted, retired, or the code changed back).</summary>
    Resolved,

    /// <summary>An automatic fix the owner undid.</summary>
    Undone,
}

/// <summary>Where a flow step is, judged against the branch checked out now.</summary>
public enum StepPlace
{
    /// <summary>In the code here.</summary>
    Here,

    /// <summary>Not in the code, and it was last seen on this branch (or the folder is not a git checkout): drift to repair.</summary>
    Gone,

    /// <summary>Not in the code, and it was last seen on another branch (or before branches were kept): kept, left alone, judged again on that branch.</summary>
    OtherBranch,

    /// <summary>A partial check did not look at this step.</summary>
    NotInspected,
}

/// <summary>The stored text forms of the repair enums, and the branch rule for steps.</summary>
public static class MemoryRepairText
{
    public static string Kind(MemoryRepairKind kind) => kind switch
    {
        MemoryRepairKind.LinkBroken => "link_broken",
        MemoryRepairKind.StepGone => "step_gone",
        MemoryRepairKind.StepMoved => "step_moved",
        MemoryRepairKind.NoteCodeChanged => "note_code_changed",
        _ => "note_flow_changed",
    };

    public static MemoryRepairKind KindFrom(string? text) => text switch
    {
        "link_broken" => MemoryRepairKind.LinkBroken,
        "step_gone" => MemoryRepairKind.StepGone,
        "step_moved" => MemoryRepairKind.StepMoved,
        "note_code_changed" => MemoryRepairKind.NoteCodeChanged,
        _ => MemoryRepairKind.NoteFlowChanged,
    };

    public static string Target(MemoryRepairTarget target) => target == MemoryRepairTarget.Flow ? "flow" : "note";

    public static MemoryRepairTarget TargetFrom(string? text) => text == "flow" ? MemoryRepairTarget.Flow : MemoryRepairTarget.Note;

    public static string State(MemoryRepairState state) => state switch
    {
        MemoryRepairState.AutoFixed => "auto_fixed",
        MemoryRepairState.Resolved => "resolved",
        MemoryRepairState.Undone => "undone",
        _ => "open",
    };

    public static MemoryRepairState StateFrom(string? text) => text switch
    {
        "auto_fixed" => MemoryRepairState.AutoFixed,
        "resolved" => MemoryRepairState.Resolved,
        "undone" => MemoryRepairState.Undone,
        _ => MemoryRepairState.Open,
    };

    /// <summary>A few words for people and agents: 'link does not call', 'step gone', ...</summary>
    public static string Describe(MemoryRepairKind kind) => kind switch
    {
        MemoryRepairKind.LinkBroken => "link does not call",
        MemoryRepairKind.StepGone => "step gone from the code",
        MemoryRepairKind.StepMoved => "step moved",
        MemoryRepairKind.NoteCodeChanged => "note's code changed",
        _ => "note's flow changed",
    };

    /// <summary>
    /// Where a step is. Found in the code: here. Missing: drift when it was last seen on the branch checked out now, or when the folder is not a git checkout
    /// (there is no other branch it could be on); left alone when it was last seen on another branch, never seen since branches were kept, or when HEAD is
    /// detached (no branch to compare with).
    /// </summary>
    public static StepPlace Judge(bool inspected, bool found, string? stepBranch, Waymark.Core.Workspaces.WorkspaceHead head)
    {
        ArgumentNullException.ThrowIfNull(head);
        if (!inspected)
        {
            return StepPlace.NotInspected;
        }

        if (found)
        {
            return StepPlace.Here;
        }

        if (!head.IsGit)
        {
            return StepPlace.Gone;
        }

        return head.Branch is not null && stepBranch is not null && string.Equals(stepBranch, head.Branch, StringComparison.Ordinal)
            ? StepPlace.Gone
            : StepPlace.OtherBranch;
    }
}

/// <summary>
/// One item of a workspace's repair queue. The target is a flow (<see cref="StepSpec"/> names the step, empty for the flow as a whole) or a note.
/// <see cref="Path"/> is the file the item is about, for hints on files an agent touches. <see cref="OwnerOnly"/> marks the owner's notes: agents cannot change them.
/// </summary>
public sealed record MemoryRepair(
    long Id,
    long WorkspaceId,
    MemoryRepairKind Kind,
    MemoryRepairTarget Target,
    long TargetId,
    string TargetName,
    int StepOrdinal,
    string StepSpec,
    string Path,
    string Detail,
    string? Suggestion,
    bool OwnerOnly,
    MemoryRepairState State,
    long Seq,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    DateTimeOffset? ResolvedUtc,
    string? ResolvedBy,
    string? UndoJson);

/// <summary>Something a check found wrong; the scanner hands these to the store, which opens new items and keeps known ones.</summary>
public sealed record RepairFinding(
    MemoryRepairKind Kind,
    MemoryRepairTarget Target,
    long TargetId,
    string TargetName,
    int StepOrdinal,
    string StepSpec,
    string Path,
    string Detail,
    string? Suggestion,
    bool OwnerOnly);

/// <summary>
/// What a check covered: a whole target (<see cref="StepSpec"/> null) or one step of a flow. Open items inside the scope that the check did not find again are resolved;
/// items outside it are left as they are.
/// </summary>
public sealed record RepairScope(MemoryRepairTarget Target, long TargetId, string? StepSpec = null);

public sealed record RepairSyncResult(int Opened, int Resolved, int Open, long Seq);

/// <summary>The queue of one workspace in numbers, for the overview, the workspace guide and the admin UI.</summary>
public sealed record RepairCounts(int Open, int ForAgents, int ForOwner, int AutoFixedRecently, int OtherBranchSteps, long Seq, string? Branch, DateTimeOffset? FullScanUtc)
{
    public static RepairCounts None { get; } = new(0, 0, 0, 0, 0, 0, null, null);
}

/// <summary>What an automatic fix changed, so the owner can undo it: the flow, the version it saved, and the step's old and new text.</summary>
public sealed record AutoFixUndo(long FlowId, string FlowName, int VersionAfter, int StepOrdinal, string OldSpec, string NewSpec, int NotesMoved);

/// <summary>How memory drift is repaired. Automatic: agents are asked to repair without asking the owner. Ask: agents offer it. Off: the queue is the owner's only.</summary>
public enum MemoryRepairMode
{
    Automatic,
    Ask,
    Off,
}

/// <param name="AutoFix">Waymark fixes the mechanical cases itself (a step that moved, with exactly one new place and its links intact).</param>
/// <param name="Budget">How many repairs an agent is asked for in one session: a suggestion, not a limit; past it the agent keeps to what its task needs.</param>
public sealed record MemoryRepairOptions(MemoryRepairMode Mode, bool AutoFix, int Budget)
{
    public const int DefaultBudget = 5;
    public const int MaxBudget = 50;

    public static MemoryRepairOptions Default { get; } = new(MemoryRepairMode.Automatic, true, DefaultBudget);

    public static string ModeText(MemoryRepairMode mode) => mode switch
    {
        MemoryRepairMode.Ask => "ask",
        MemoryRepairMode.Off => "off",
        _ => "automatic",
    };

    public static MemoryRepairMode ModeFrom(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "ask" => MemoryRepairMode.Ask,
        "off" => MemoryRepairMode.Off,
        _ => MemoryRepairMode.Automatic,
    };
}

/// <summary>The owner's repair settings: mode, automatic fixes and the per-session budget. Read often, so implementations cache briefly.</summary>
public interface IMemoryRepairSettings
{
    Task<MemoryRepairOptions> GetAsync(CancellationToken ct);

    Task SetAsync(MemoryRepairOptions options, string changedBy, CancellationToken ct);
}

/// <summary>
/// The repair queue of each workspace. Every write runs on the write queue. An open item is unique per (workspace, kind, target, step); finding it again
/// only updates when it was last seen, so its seq stays and a client is told about it once.
/// </summary>
public interface IMemoryRepairStore
{
    /// <summary>
    /// Brings the open items inside <paramref name="scopes"/> in line with what a check <paramref name="found"/>: new findings are opened (seq rises once for the batch),
    /// known ones are kept, and open items in scope that were not found again are resolved by <paramref name="resolvedBy"/>.
    /// </summary>
    Task<RepairSyncResult> SyncAsync(long workspaceId, IReadOnlyCollection<RepairScope> scopes, IReadOnlyList<RepairFinding> found, string resolvedBy, DateTimeOffset now, CancellationToken ct);

    /// <summary>Resolves every open item of a target (a deleted flow, a retired note).</summary>
    Task<int> ResolveTargetAsync(long workspaceId, MemoryRepairTarget target, long targetId, string resolvedBy, DateTimeOffset now, CancellationToken ct);

    /// <summary>Records an automatic fix: an item in state auto-fixed with its undo record, and any open item for the same step resolved.</summary>
    Task<long> RecordAutoFixAsync(long workspaceId, RepairFinding finding, AutoFixUndo undo, DateTimeOffset now, CancellationToken ct);

    /// <summary>Marks an automatic fix undone.</summary>
    Task<bool> MarkUndoneAsync(long workspaceId, long id, DateTimeOffset now, CancellationToken ct);

    /// <summary>True when the owner undid an automatic fix that moved this flow's step from <paramref name="oldSpec"/> to <paramref name="newSpec"/>: it is never made again.</summary>
    Task<bool> WasUndoneAsync(long workspaceId, long flowId, string oldSpec, string newSpec, CancellationToken ct);

    /// <summary>A page of open items, oldest first. <paramref name="ownerOnly"/> null for all, true for the owner's, false for what agents may repair.</summary>
    Task<IReadOnlyList<MemoryRepair>> ListOpenAsync(long workspaceId, bool? ownerOnly, int skip, int take, CancellationToken ct);

    /// <summary>Open items about one file, for a hint when an agent touches it.</summary>
    Task<IReadOnlyList<MemoryRepair>> ListOpenForPathAsync(long workspaceId, string path, int take, CancellationToken ct);

    /// <summary>The newest automatic fixes, newest first.</summary>
    Task<IReadOnlyList<MemoryRepair>> ListAutoFixedAsync(long workspaceId, int take, CancellationToken ct);

    Task<MemoryRepair?> GetAsync(long workspaceId, long id, CancellationToken ct);

    /// <summary>The numbers of one workspace.</summary>
    Task<RepairCounts> CountsAsync(long workspaceId, DateTimeOffset recentSince, CancellationToken ct);

    /// <summary>The numbers of every workspace with anything to say, in one query each, for the overview.</summary>
    Task<IReadOnlyDictionary<long, RepairCounts>> CountsAllAsync(DateTimeOffset recentSince, CancellationToken ct);

    /// <summary>After a full scan: how many steps were left alone as on another branch, the branch checked out, and when.</summary>
    Task SetFullScanAsync(long workspaceId, int otherBranchSteps, string? branch, DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// What each agent did in this session, as far as repairs need it: the files it read (a note's code must be read before the note is confirmed) and how many
/// repairs it made (against the budget). In memory only, per client and workspace; forgotten after a few hours without calls.
/// </summary>
public interface IMemoryRepairLedger
{
    void RecordRead(string client, string workspace, string path);

    bool HasRead(string client, string workspace, string path);

    void RecordRepair(string client, string workspace);

    int RepairsDone(string client, string workspace);

    /// <summary>
    /// Forgets that agents read these files of a workspace (their code changed, so a read before the change says nothing about the code now); null = every file.
    /// </summary>
    void ForgetReads(string workspace, IReadOnlyCollection<string>? paths);
}
