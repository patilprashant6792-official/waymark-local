using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Waymark.Core.Code;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Memory.Repairs;

/// <summary>
/// Keeps each workspace's repair queue true to the code. It listens for every change that can make memory drift and re-checks only what the change can
/// have touched, with the same <see cref="FlowInspector"/> and note check memory_recall uses, so the queue and what agents read always agree.
/// <list type="bullet">
/// <item>Files changed on disk (any source: editor, git, agents; <see cref="CodeFilesChanged"/>, <see cref="FileChanged"/>): the flow steps and notes anchored in them.</item>
/// <item>A flow saved or a note saved: that flow (with its flow notes) or that note. A flow deleted or a note retired: its items are resolved.</item>
/// <item>A branch switch, a folder change, an import, a workspace switched on, and start-up: the whole workspace, within a budget.</item>
/// </list>
/// Handlers only queue (they run on the publisher's flow); changes are collected for <see cref="Quiet"/> of quiet and each workspace gets one pass.
/// A step last seen on another branch is never queued; it is counted for the owner and judged again when its branch is checked out.
/// A step that moved to exactly one other place is repaired by <see cref="MemoryAutoRepair"/> when automatic fixes are on.
/// </summary>
internal sealed class MemoryDriftScanner(
    IEventBus events,
    IWorkspaceStore workspaces,
    IFeatureMapStore maps,
    INoteStore notes,
    INoteService noteChecks,
    FlowInspector inspector,
    MemoryAutoRepair autoRepair,
    FlowDriftFinder drift,
    IMemoryRepairStore repairs,
    IMemoryRepairSettings settings,
    IMemoryRepairLedger ledger,
    TimeProvider time,
    ILogger<MemoryDriftScanner> logger) : BackgroundService
{
    internal static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    /// <summary>The most flows and notes one whole-workspace pass checks; the rest are checked when they are next touched or read.</summary>
    internal const int MaxFlowsPerPass = 1_000;
    internal const int MaxNotesPerPass = 2_000;
    internal const int MaxStepHitsPerPath = 200;
    internal const int MaxNoteHitsPerPath = 200;

    internal const string Checker = "waymark (re-check)";

    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<IDisposable> _subscriptions = [];

    internal enum SignalKind
    {
        Path,
        Whole,
        Flow,
        FlowDeleted,
        Note,
        NoteRetired,
    }

    internal readonly record struct Signal(long WorkspaceId, SignalKind Kind, string? Path = null, long Id = 0);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _subscriptions.Add(events.Subscribe<CodeFilesChanged>(OnFilesChanged));
        _subscriptions.Add(events.Subscribe<FileChanged>(changed => Post(new Signal(changed.WorkspaceId, SignalKind.Path, changed.RelativePath))));
        _subscriptions.Add(events.Subscribe<FolderChanged>(changed => Post(new Signal(changed.WorkspaceId, SignalKind.Whole))));
        _subscriptions.Add(events.Subscribe<BranchChanged>(changed => Post(new Signal(changed.WorkspaceId, SignalKind.Whole))));
        _subscriptions.Add(events.Subscribe<FlowChanged>(OnFlowChanged));
        _subscriptions.Add(events.Subscribe<NoteChanged>(OnNoteChanged));
        _subscriptions.Add(events.Subscribe<WorkspaceChanged>(OnWorkspaceChanged));
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        _signals.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let the start-up reconcile of the code index settle first; then every enabled workspace once, for what changed while Waymark was off.
            await Task.Delay(StartupDelay, time, stoppingToken).ConfigureAwait(false);
            foreach (var workspace in await workspaces.ListAsync(stoppingToken).ConfigureAwait(false))
            {
                if (workspace.Enabled)
                {
                    await Post(new Signal(workspace.Id, SignalKind.Whole)).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The start-up memory check could not list the workspaces; changes are still checked as they happen.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await _signals.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }

                var due = await CollectAsync(stoppingToken).ConfigureAwait(false);
                foreach (var (workspaceId, work) in due)
                {
                    try
                    {
                        await ScanAsync(workspaceId, work, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One workspace that fails must not stop the others: reported, and its next change tries again.
                        logger.LogError(ex, "Checking the memory of workspace {WorkspaceId} for drift failed; its next change tries again.", workspaceId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The memory drift check stopped a pass unexpectedly; it continues with the next change.");
            }
        }
    }

    // Takes signals until none came for the quiet time, so a burst (a save, a checkout, a build) is one pass per workspace.
    private async Task<Dictionary<long, ScanWork>> CollectAsync(CancellationToken ct)
    {
        var due = new Dictionary<long, ScanWork>();
        while (true)
        {
            while (_signals.Reader.TryRead(out var signal))
            {
                if (!due.TryGetValue(signal.WorkspaceId, out var work))
                {
                    work = new ScanWork();
                    due[signal.WorkspaceId] = work;
                }

                work.Add(signal);
            }

            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quiet.CancelAfter(Quiet);
            try
            {
                if (!await _signals.Reader.WaitToReadAsync(quiet.Token).ConfigureAwait(false))
                {
                    return due;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return due;
            }
        }
    }

    /// <summary>One pass over one workspace. Internal so tests can run a pass without the timing.</summary>
    internal async Task ScanAsync(long workspaceId, ScanWork work, CancellationToken ct)
    {
        var workspace = (await workspaces.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Id == workspaceId);
        if (workspace is null || !workspace.Enabled)
        {
            return;
        }

        var now = time.GetUtcNow();
        var changed = false;
        foreach (var id in work.DeletedFlows)
        {
            changed |= await repairs.ResolveTargetAsync(workspace.Id, MemoryRepairTarget.Flow, id, Checker, now, ct).ConfigureAwait(false) > 0;
        }

        foreach (var id in work.RetiredNotes)
        {
            changed |= await repairs.ResolveTargetAsync(workspace.Id, MemoryRepairTarget.Note, id, Checker, now, ct).ConfigureAwait(false) > 0;
        }

        // Reads of code that changed since say nothing about the code now: a note on it must be read again before it is confirmed.
        ledger.ForgetReads(workspace.Name, work.Whole ? null : [.. work.Paths]);

        var options = await settings.GetAsync(ct).ConfigureAwait(false);
        var head = inspector.HeadOf(workspace);
        var pass = new Pass(workspace, head);
        if (work.Whole)
        {
            await SweepOrphansAsync(pass, ct).ConfigureAwait(false);
        }
        await CheckFlowsAsync(pass, work, ct).ConfigureAwait(false);
        await NameAllCandidatesAsync(pass, ct).ConfigureAwait(false);
        await CheckNotesAsync(pass, work, ct).ConfigureAwait(false);
        await MoveNotesOfRepointedStepsAsync(pass, work, ct).ConfigureAwait(false);
        // Mechanical repairs first: a step that moved to one clear place. What was fixed leaves the findings; its open item is resolved by the fix itself.
        var fixedCount = 0;
        if (options.AutoFix)
        {
            foreach (var finding in pass.Findings.Where(f => f.Kind == MemoryRepairKind.StepMoved).ToList())
            {
                var oldSpec = finding.StepSpec;
                var undo = await autoRepair.TryMoveStepAsync(workspace, finding.TargetId, oldSpec, ct).ConfigureAwait(false);
                if (undo is not null)
                {
                    pass.Findings.Remove(finding);
                    fixedCount++;
                }
            }
        }

        foreach (var (mapId, specs) in pass.Stamps)
        {
            if (head is { IsGit: true, Branch: { } branch } && specs.Count > 0)
            {
                await maps.StampBranchAsync(workspace.Id, mapId, specs, branch, ct).ConfigureAwait(false);
            }
        }

        var result = await repairs.SyncAsync(workspace.Id, pass.Scopes, pass.Findings, Checker, now, ct).ConfigureAwait(false);
        if (work.Whole)
        {
            await repairs.SetFullScanAsync(workspace.Id, pass.OtherBranchSteps, head.Branch, now, ct).ConfigureAwait(false);
        }

        if (changed || fixedCount > 0 || result.Opened > 0 || result.Resolved > 0 || work.Whole)
        {
            await events.PublishAsync(new MemoryRepairsChanged(workspace.Id, result.Open, result.Seq)).ConfigureAwait(false);
        }

        if (result.Opened > 0 || result.Resolved > 0 || fixedCount > 0)
        {
            logger.LogInformation(
                "Memory of {Workspace}: {Opened} repair(s) opened, {Resolved} resolved, {Fixed} fixed automatically; {Open} open.",
                workspace.Name,
                result.Opened,
                result.Resolved,
                fixedCount,
                result.Open);
        }
    }

    // A whole pass also answers open items whose target is gone (a flow deleted or a note retired or replaced while Waymark was off, or by an import).
    private async Task SweepOrphansAsync(Pass pass, CancellationToken ct)
    {
        var open = await repairs.ListOpenAsync(pass.Workspace.Id, null, 0, 500, ct).ConfigureAwait(false);
        var flowIds = open.Where(item => item.Target == MemoryRepairTarget.Flow).Select(item => item.TargetId).Distinct().ToList();
        var noteIds = open.Where(item => item.Target == MemoryRepairTarget.Note).Select(item => item.TargetId).Distinct().ToList();
        if (flowIds.Count > 0)
        {
            var present = (await maps.GetAsync(pass.Workspace.Id, flowIds, ct).ConfigureAwait(false)).Select(map => map.Id).ToHashSet();
            foreach (var id in flowIds.Where(id => !present.Contains(id)))
            {
                pass.Scopes.Add(new RepairScope(MemoryRepairTarget.Flow, id));
            }
        }

        if (noteIds.Count > 0)
        {
            var active = (await notes.GetAsync(pass.Workspace.Id, noteIds, ct).ConfigureAwait(false)).Where(note => note.IsActive).Select(note => note.Id).ToHashSet();
            foreach (var id in noteIds.Where(id => !active.Contains(id)))
            {
                pass.Scopes.Add(new RepairScope(MemoryRepairTarget.Note, id));
            }
        }
    }

    // A step that moved to several places: the item names them all, so an agent does not take the first one found for the right one.
    private async Task NameAllCandidatesAsync(Pass pass, CancellationToken ct)
    {
        for (var i = 0; i < pass.Findings.Count; i++)
        {
            var finding = pass.Findings[i];
            if (finding.Kind != MemoryRepairKind.StepMoved || !FeatureComponentSpec.TryParse(finding.StepSpec, out var component, out _))
            {
                continue;
            }

            var candidates = await drift.FindAllAsync(pass.Workspace.Id, component, ct).ConfigureAwait(false);
            if (candidates.Count > 1)
            {
                pass.Findings[i] = finding with
                {
                    Suggestion = string.Join("; ", candidates),
                    Detail = $"{finding.Detail}; a method of that name is in {candidates.Count} places",
                };
            }
        }
    }

    // A flow saved by an agent that took the suggestion of an open 'moved' item (the old step replaced by the one new place): the step notes follow it,
    // as they do for an automatic fix, instead of being left on a step the flow no longer has.
    private async Task MoveNotesOfRepointedStepsAsync(Pass pass, ScanWork work, CancellationToken ct)
    {
        if (work.Flows.Count == 0)
        {
            return;
        }

        var open = (await repairs.ListOpenAsync(pass.Workspace.Id, null, 0, 500, ct).ConfigureAwait(false))
            .Where(item => item.Kind == MemoryRepairKind.StepMoved && item.Target == MemoryRepairTarget.Flow && work.Flows.Contains(item.TargetId) && item.Suggestion is not null)
            .ToList();
        if (open.Count == 0)
        {
            return;
        }

        var flows = (await maps.GetAsync(pass.Workspace.Id, [.. open.Select(item => item.TargetId).Distinct()], ct).ConfigureAwait(false)).ToDictionary(map => map.Id);
        foreach (var item in open)
        {
            if (!flows.TryGetValue(item.TargetId, out var map) || map.Steps.Any(step => string.Equals(step.Spec, item.StepSpec, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var candidates = item.Suggestion!.Split("; ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var taken = candidates.Where(candidate => map.Steps.Any(step => string.Equals(step.Spec, candidate, StringComparison.OrdinalIgnoreCase))).ToList();
            if (taken.Count == 1)
            {
                await notes.RebaseStepAsync(pass.Workspace.Id, map.Id, item.StepSpec, taken[0], ct).ConfigureAwait(false);
            }
        }
    }
    private async Task CheckFlowsAsync(Pass pass, ScanWork work, CancellationToken ct)
    {
        var whole = new HashSet<long>(work.Flows);
        if (work.Whole)
        {
            whole.UnionWith(await maps.ListIdsAsync(pass.Workspace.Id, MaxFlowsPerPass, ct).ConfigureAwait(false));
        }

        // Steps in changed files: only those steps and the steps they call (whose links from them are judged); InspectSelectedAsync adds their parents.
        var partial = new Dictionary<long, HashSet<string>>();
        if (!work.Whole)
        {
            foreach (var path in work.Paths)
            {
                foreach (var hit in await maps.FindByPathAsync(pass.Workspace.Id, path, MaxStepHitsPerPath, ct).ConfigureAwait(false))
                {
                    if (whole.Contains(hit.FlowId))
                    {
                        continue;
                    }

                    if (!partial.TryGetValue(hit.FlowId, out var specs))
                    {
                        specs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        partial[hit.FlowId] = specs;
                    }

                    specs.Add(hit.Spec);
                }
            }
        }

        var ids = whole.Concat(partial.Keys).Distinct().ToList();
        foreach (var chunk in ids.Chunk(50))
        {
            foreach (var map in await maps.GetAsync(pass.Workspace.Id, chunk, ct).ConfigureAwait(false))
            {
                if (whole.Contains(map.Id))
                {
                    var report = await inspector.InspectAsync(pass.Workspace, map, pass.Files, ct).ConfigureAwait(false);
                    pass.Scopes.Add(new RepairScope(MemoryRepairTarget.Flow, map.Id));
                    Classify(pass, report, Enumerable.Range(0, report.Steps.Count).ToHashSet(), countOtherBranch: work.Whole);
                }
                else if (partial.TryGetValue(map.Id, out var touchedSpecs))
                {
                    var steps = FlowStepText.FromStored(map.Steps);
                    var touched = Enumerable.Range(0, steps.Count).Where(i => touchedSpecs.Contains(steps[i].Spec)).ToHashSet();
                    var judged = new HashSet<int>(touched);
                    for (var i = 1; i < steps.Count; i++)
                    {
                        if (FlowStepText.ParentIndex(steps, i) is { } parent && touched.Contains(parent))
                        {
                            judged.Add(i);
                        }
                    }

                    var report = await inspector.InspectSelectedAsync(pass.Workspace, map, judged, pass.Files, ct).ConfigureAwait(false);
                    foreach (var index in judged)
                    {
                        pass.Scopes.Add(new RepairScope(MemoryRepairTarget.Flow, map.Id, steps[index].Spec));
                    }

                    Classify(pass, report, judged, countOtherBranch: false);
                }
            }
        }
    }

    // What each judged step says: here (and maybe seen on a new branch), gone or moved, on another branch, and whether its link from the step above holds.
    private static void Classify(Pass pass, FlowReport report, IReadOnlySet<int> judged, bool countOtherBranch)
    {
        var map = report.Map;
        var stamps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in judged.Order())
        {
            if (index < 0 || index >= report.Steps.Count)
            {
                continue;
            }

            var step = report.Steps[index];
            var view = report.Views[index];
            var path = step.Valid ? step.Component.Path : MemoryRepairLedger.FileOf(step.Spec);
            switch (report.Place(index))
            {
                case StepPlace.Here:
                    if (pass.Head is { IsGit: true, Branch: { } branch } && index < map.Steps.Count && !string.Equals(map.Steps[index].Branch, branch, StringComparison.Ordinal))
                    {
                        stamps.Add(step.Spec);
                    }

                    if (map.Ordered && index > 0 && report.Links[index] is { State: FlowLinkState.DoesNotCall } link)
                    {
                        var parent = FlowStepText.ParentIndex(report.Steps, index) ?? 0;
                        pass.Findings.Add(new RepairFinding(
                            MemoryRepairKind.LinkBroken,
                            MemoryRepairTarget.Flow,
                            map.Id,
                            map.Name,
                            index,
                            step.Spec,
                            path,
                            $"link {parent + 1} -> {index + 1} no longer holds: {link.Detail}",
                            null,
                            false));
                    }

                    break;
                case StepPlace.Gone:
                    var moved = report.Moved[index];
                    pass.Findings.Add(new RepairFinding(
                        moved is null ? MemoryRepairKind.StepGone : MemoryRepairKind.StepMoved,
                        MemoryRepairTarget.Flow,
                        map.Id,
                        map.Name,
                        index,
                        step.Spec,
                        path,
                        $"step {index + 1} is no longer in the code on this branch: {view.Problem ?? "not found"}",
                        moved,
                        false));
                    break;
                case StepPlace.OtherBranch:
                    if (countOtherBranch)
                    {
                        pass.OtherBranchSteps++;
                    }

                    break;
            }
        }

        if (stamps.Count > 0)
        {
            if (!pass.Stamps.TryGetValue(map.Id, out var known))
            {
                known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                pass.Stamps[map.Id] = known;
            }

            known.UnionWith(stamps);
        }
    }

    private async Task CheckNotesAsync(Pass pass, ScanWork work, CancellationToken ct)
    {
        var byId = new Dictionary<long, Note>();
        if (work.Whole)
        {
            foreach (var note in (await notes.ListActiveAsync(pass.Workspace.Id, ct).ConfigureAwait(false)).Take(MaxNotesPerPass))
            {
                byId[note.Id] = note;
            }
        }
        else
        {
            var ids = new HashSet<long>(work.Notes);
            foreach (var path in work.Paths)
            {
                ids.UnionWith(await notes.FindActiveByPathAsync(pass.Workspace.Id, path, MaxNoteHitsPerPath, ct).ConfigureAwait(false));
            }

            foreach (var chunk in ids.Where(id => id > 0).Chunk(200))
            {
                foreach (var note in await notes.GetAsync(pass.Workspace.Id, chunk, ct).ConfigureAwait(false))
                {
                    byId[note.Id] = note;
                }
            }

            // A flow that was saved again makes its flow notes 'may be stale (flow changed)'.
            if (work.Flows.Count > 0)
            {
                foreach (var note in await notes.ListForFlowsAsync(pass.Workspace.Id, [.. work.Flows], ct).ConfigureAwait(false))
                {
                    byId[note.Id] = note;
                }
            }
        }

        var active = new List<Note>();
        foreach (var note in byId.Values)
        {
            if (note.IsActive)
            {
                active.Add(note);
            }
            else
            {
                // A revision replaced it or it was retired: whatever was open for this id is answered.
                pass.Scopes.Add(new RepairScope(MemoryRepairTarget.Note, note.Id));
            }
        }

        // A detached HEAD is a look at an old commit, not a branch the notes were written for: like steps, notes are not judged there (their items stay as they are).
        if (active.Count == 0 || pass.Head is { IsGit: true, Branch: null })
        {
            return;
        }

        var staleness = await noteChecks.CheckAsync(pass.Workspace, active, ct).ConfigureAwait(false);
        foreach (var note in active)
        {
            pass.Scopes.Add(new RepairScope(MemoryRepairTarget.Note, note.Id));

            // The revision this one replaced is answered by it: its open items go.
            if (note.ReplacesId is { } replaced)
            {
                pass.Scopes.Add(new RepairScope(MemoryRepairTarget.Note, replaced));
            }
            if (!staleness.TryGetValue(note.Id, out var judged) || !judged.MayBeStale)
            {
                continue;
            }

            var flowChanged = string.Equals(judged.Reason, "flow changed", StringComparison.Ordinal);
            var anchor = NoteAnchors.CodeSpecOf(note);
            pass.Findings.Add(new RepairFinding(
                flowChanged ? MemoryRepairKind.NoteFlowChanged : MemoryRepairKind.NoteCodeChanged,
                MemoryRepairTarget.Note,
                note.Id,
                note.Title.Length > 0 ? note.Title : NoteRules.OneLine(note.Body, NoteRules.MaxTitleChars),
                -1,
                string.Empty,
                anchor is null ? string.Empty : MemoryRepairLedger.FileOf(anchor),
                $"{judged.Reason ?? "changed"} since it was written or confirmed ({NoteLines.Anchor(note)})",
                anchor,
                note.Author.IsHuman));
        }
    }

    private ValueTask Post(Signal signal)
    {
        _signals.Writer.TryWrite(signal);
        return ValueTask.CompletedTask;
    }

    private ValueTask OnFilesChanged(CodeFilesChanged changed)
    {
        if (changed.Whole)
        {
            return Post(new Signal(changed.WorkspaceId, SignalKind.Whole));
        }

        foreach (var path in changed.Paths)
        {
            _signals.Writer.TryWrite(new Signal(changed.WorkspaceId, SignalKind.Path, path));
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask OnFlowChanged(FlowChanged changed) => changed.Change switch
    {
        FlowChange.Deleted => Post(new Signal(changed.WorkspaceId, SignalKind.FlowDeleted, Id: changed.FlowId)),
        FlowChange.Imported => Post(new Signal(changed.WorkspaceId, SignalKind.Whole)),
        _ => Post(new Signal(changed.WorkspaceId, SignalKind.Flow, Id: changed.FlowId)),
    };

    private ValueTask OnNoteChanged(NoteChanged changed) => changed.Change switch
    {
        NoteChange.Retired => Post(new Signal(changed.WorkspaceId, SignalKind.NoteRetired, Id: changed.NoteId)),
        NoteChange.Imported => Post(new Signal(changed.WorkspaceId, SignalKind.Whole)),
        _ => changed.NoteId > 0 ? Post(new Signal(changed.WorkspaceId, SignalKind.Note, Id: changed.NoteId)) : ValueTask.CompletedTask,
    };

    private ValueTask OnWorkspaceChanged(WorkspaceChanged changed) =>
        changed.Kind is WorkspaceChangeKind.Added or WorkspaceChangeKind.Enabled
            ? Post(new Signal(changed.WorkspaceId, SignalKind.Whole))
            : ValueTask.CompletedTask;

    /// <summary>What one workspace's pass has to look at, merged from the signals of a burst.</summary>
    internal sealed class ScanWork
    {
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<long> Flows { get; } = [];

        public HashSet<long> DeletedFlows { get; } = [];

        public HashSet<long> Notes { get; } = [];

        public HashSet<long> RetiredNotes { get; } = [];

        public bool Whole { get; set; }

        public static ScanWork Everything() => new() { Whole = true };

        internal void Add(Signal signal)
        {
            switch (signal.Kind)
            {
                case SignalKind.Whole:
                    Whole = true;
                    break;
                case SignalKind.Path when signal.Path is { Length: > 0 } path:
                    Paths.Add(MemoryRepairLedger.FileOf(path));
                    break;
                case SignalKind.Flow:
                    Flows.Add(signal.Id);
                    break;
                case SignalKind.FlowDeleted:
                    DeletedFlows.Add(signal.Id);
                    Flows.Remove(signal.Id);
                    break;
                case SignalKind.Note:
                    Notes.Add(signal.Id);
                    break;
                case SignalKind.NoteRetired:
                    RetiredNotes.Add(signal.Id);
                    Notes.Remove(signal.Id);
                    break;
            }
        }
    }

    private sealed class Pass(Workspace workspace, WorkspaceHead head)
    {
        public Workspace Workspace { get; } = workspace;

        public WorkspaceHead Head { get; } = head;

        public Dictionary<string, FileView> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<RepairFinding> Findings { get; } = [];

        public List<RepairScope> Scopes { get; } = [];

        public Dictionary<long, HashSet<string>> Stamps { get; } = [];

        public int OtherBranchSteps { get; set; }
    }
}
