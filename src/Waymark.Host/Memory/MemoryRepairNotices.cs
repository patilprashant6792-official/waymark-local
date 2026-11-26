using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Host.Memory;

/// <summary>
/// Brings the repair queue to the agent where it works. After every tool call in a workspace (by the call pipeline, after the guide), at most one 🔧 line:
/// <list type="number">
/// <item>a hint when the call named a file that has open repairs (the agent has that code open: the best moment to fix), even when the budget is used;</item>
/// <item>otherwise, once, when the agent passed the suggested number of repairs, that it should keep to what its task needs;</item>
/// <item>otherwise a notice when the workspace's queue has items this client was not told about yet (once per new batch, by the queue's seq).</item>
/// </list>
/// It also feeds the ledger: the files code tools read (a note's code must be read before it is confirmed) and the memory_save calls that answered an open
/// item (the budget; saving a flow or note with nothing open is not a repair).
/// The lines are facts and pointers; what the agent may do without asking comes from the server instructions and the mode the workspace guide states.
/// Mode off: nothing is added. A failure here never fails the call: the line is left out.
/// </summary>
public sealed class MemoryRepairNotices(
    IWorkspaceService workspaces,
    IMemoryRepairStore repairs,
    IMemoryRepairSettings settings,
    IMemoryRepairLedger ledger,
    ILogger<MemoryRepairNotices> logger)
{
    private const string PathArgument = "relativeFilePath";
    private const string ActionArgument = "action";
    private const int MaxPathHint = 3;
    private const int MaxOpenLookup = 500;

    private static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal) { "code_method", "code_read", "code_outline" };

    private static readonly HashSet<string> RepairActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "save_feature_map", "delete_feature_map", "edit_note", "retire_note", "confirm_note",
    };

    // What each client was last told per workspace: the queue's seq, and whether it heard that its budget is used.
    private readonly ConcurrentDictionary<(string Client, long Workspace), Told> _told = new();

    /// <summary>Records what the call did and adds at most one 🔧 line. Returns the text added, or null.</summary>
    public async Task<string?> AfterCallAsync(
        CallToolResult result,
        string tool,
        string client,
        string? workspaceName,
        IDictionary<string, JsonElement>? arguments,
        bool succeeded,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (workspaceName is null)
        {
            return null;
        }

        try
        {
            var paths = PathsOf(arguments);
            if (succeeded && ReadTools.Contains(tool))
            {
                foreach (var path in paths)
                {
                    ledger.RecordRead(client, workspaceName, path);
                }
            }

            var workspace = (await workspaces.ListAsync(ct)).FirstOrDefault(w => string.Equals(w.Name, workspaceName, StringComparison.OrdinalIgnoreCase));
            if (workspace is null)
            {
                return null;
            }

            if (succeeded)
            {
                await RecordRepairAsync(tool, client, workspace, arguments, ct);
            }

            var options = await settings.GetAsync(ct);
            if (options.Mode == MemoryRepairMode.Off || !workspace.Enabled)
            {
                return null;
            }

            // The agent is reading the worklist itself: it now knows everything up to here, so no line on top.
            if (string.Equals(tool, "memory_recall", StringComparison.Ordinal) && string.Equals(ActionOf(arguments), "repairs", StringComparison.OrdinalIgnoreCase))
            {
                var counts = await repairs.CountsAsync(workspace.Id, DateTimeOffset.MinValue, ct);
                _told.GetOrAdd((client.ToLowerInvariant(), workspace.Id), _ => new Told()).Seq = counts.Seq;
                return null;
            }

            var line = await LineAsync(workspace, client, paths, options, ct);
            if (line is null)
            {
                return null;
            }

            result.Content.Add(new TextContentBlock { Text = line });
            return line;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The memory repair line for a {Tool} result was left out", tool);
            return null;
        }
    }

    private async Task<string?> LineAsync(Workspace workspace, string client, IReadOnlyList<string> paths, MemoryRepairOptions options, CancellationToken ct)
    {
        var key = (client.ToLowerInvariant(), workspace.Id);
        var told = _told.GetOrAdd(key, _ => new Told());
        var done = ledger.RepairsDone(client, workspace.Name);
        var budgetUsed = done >= options.Budget;
        var counts = await repairs.CountsAsync(workspace.Id, DateTimeOffset.MinValue, ct);
        if (counts.ForAgents == 0)
        {
            return null;
        }

        // 1. A file the agent is working in has open items: the moment it can fix them best. Shown even when the budget is used: it is cheapest now.
        foreach (var path in paths.Take(MaxPathHint))
        {
            var here = await repairs.ListOpenForPathAsync(workspace.Id, path, MaxPathHint, ct);
            var forAgents = here.Where(item => !item.OwnerOnly).ToList();
            if (forAgents.Count == 0)
            {
                continue;
            }

            told.Seq = Math.Max(told.Seq, counts.Seq);
            var what = string.Join("; ", forAgents.Select(Describe));
            var ask = budgetUsed
                ? "You passed the suggested number of repairs for this session, but this one is in code you have open: fix it now, it is cheapest while you are here."
                : Ask(options.Mode);
            return $"🔧 Memory drift in {path}, which you have open: {what}. {ask} memory_recall(action='repairs') gives the fix.";
        }

        // 2. Once, when the suggested number is passed: keep to what the task needs.
        if (budgetUsed)
        {
            if (told.BudgetNoticeGiven)
            {
                return null;
            }

            told.BudgetNoticeGiven = true;
            told.Seq = counts.Seq;
            return $"🔧 Memory repairs: you passed the suggested {options.Budget} repairs for this session. Keep repairing what your task needs; other items in '{workspace.Name}' can wait for another session.";
        }

        // 3. New items since this client last heard.
        if (counts.Seq > told.Seq)
        {
            told.Seq = counts.Seq;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"🔧 Memory drift in '{workspace.Name}': {counts.ForAgents} repair(s) waiting (code changed since flows or notes were saved). {Ask(options.Mode)} memory_recall(action='repairs').");
        }

        return null;
    }

    private static string Ask(MemoryRepairMode mode) => mode == MemoryRepairMode.Automatic
        ? "The owner approved repairs (automatic mode): fix what you have open now and the rest before your final answer."
        : "Offer the owner to repair them.";

    private static string Describe(MemoryRepair item) => item.Target == MemoryRepairTarget.Flow
        ? $"flow '{item.TargetName}' step {item.StepOrdinal + 1} {MemoryRepairText.Describe(item.Kind)}"
        : $"note #{item.TargetId} '{item.TargetName}' {MemoryRepairText.Describe(item.Kind)}";

    // A memory_save call counts against the budget only when it answered an open item: a save of a flow or note that had one. Documenting a new feature does not.
    private async Task RecordRepairAsync(string tool, string client, Workspace workspace, IDictionary<string, JsonElement>? arguments, CancellationToken ct)
    {
        if (!string.Equals(tool, "memory_save", StringComparison.Ordinal) || ActionOf(arguments) is not { } action || !RepairActions.Contains(action))
        {
            return;
        }

        var flowName = StringOf(arguments, "name");
        var noteId = LongOf(arguments, "noteId");
        if (flowName is null && noteId is null)
        {
            return;
        }

        // The re-check that closes the item runs seconds after the save, so the item is still open here.
        var open = await repairs.ListOpenAsync(workspace.Id, false, 0, MaxOpenLookup, ct);
        var answered = open.Any(item => item.Target == MemoryRepairTarget.Flow
            ? flowName is not null && string.Equals(item.TargetName, flowName, StringComparison.OrdinalIgnoreCase)
            : noteId is not null && item.TargetId == noteId);
        if (answered)
        {
            ledger.RecordRepair(client, workspace.Name);
        }
    }

    private static string? StringOf(IDictionary<string, JsonElement>? arguments, string name) =>
        arguments is not null && arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text.Trim()
            : null;

    private static long? LongOf(IDictionary<string, JsonElement>? arguments, string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    // relativeFilePath may name several files, comma separated (code_outline and code_method batch them).
    private static IReadOnlyList<string> PathsOf(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || !arguments.TryGetValue(PathArgument, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return [];
        }

        return [.. (value.GetString() ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => path.Replace('\\', '/').TrimStart('/'))
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string? ActionOf(IDictionary<string, JsonElement>? arguments) =>
        arguments is not null && arguments.TryGetValue(ActionArgument, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim().Replace('-', '_').Replace(' ', '_')
            : null;

    private sealed class Told
    {
        public long Seq { get; set; }

        public bool BudgetNoticeGiven { get; set; }
    }
}
