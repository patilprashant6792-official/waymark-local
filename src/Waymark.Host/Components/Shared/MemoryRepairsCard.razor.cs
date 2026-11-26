using Microsoft.AspNetCore.Components;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;
using Waymark.Host.Components.Ui;
using Waymark.Host.Memory;

namespace Waymark.Host.Components.Shared;

/// <summary>The Memory repairs card of a workspace's Overview. It reloads when the queue changes and after each of the owner's actions.</summary>
public partial class MemoryRepairsCard : LiveComponentBase
{
    private static readonly IReadOnlyList<WmOption<MemoryRepairMode>> ModeOptions =
    [
        new(MemoryRepairMode.Automatic, "Automatic (agents repair without asking)"),
        new(MemoryRepairMode.Ask, "Ask (agents offer to repair)"),
        new(MemoryRepairMode.Off, "Off (I repair)"),
    ];

    private static readonly IReadOnlyList<WmOption<int>> BudgetOptions = [.. new[] { 1, 3, 5, 10, 20, 50 }.Select(n => new WmOption<int>(n, $"{n} repairs"))];

    private MemoryRepairOptions? _options;
    private RepairCounts? _counts;
    private IReadOnlyList<MemoryRepair> _open = [];
    private IReadOnlyList<MemoryRepair> _fixed = [];
    private string? _error;
    private bool _busy;

    [Parameter, EditorRequired]
    public Workspace Workspace { get; set; } = default!;

    [Inject]
    private MemoryRepairAdminService Service { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private ILogger<MemoryRepairsCard> Logger { get; set; } = default!;

    protected override void OnInitialized() =>
        RefreshOn<MemoryRepairsChanged>(changed => changed.WorkspaceId == Workspace.Id ? LoadAsync() : Task.CompletedTask);

    protected override Task OnParametersSetAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            _options = await Service.GetOptionsAsync(Cancel);
            _counts = await Service.CountsAsync(Workspace.Id, Cancel);
            _open = await Service.OpenAsync(Workspace.Id, Cancel);
            _fixed = await Service.AutoFixedAsync(Workspace.Id, Cancel);
            _error = null;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The memory repairs of workspace {Workspace} could not be loaded", Workspace.Name);
            _error = "The memory repairs could not be loaded.";
        }
    }

    private Task SetModeAsync(MemoryRepairMode mode) => SaveAsync(current => current with { Mode = mode });

    private Task SetBudgetAsync(int budget) => SaveAsync(current => current with { Budget = budget });

    private Task SetAutoFixAsync(bool on) => SaveAsync(current => current with { AutoFix = on });

    private Task SaveAsync(Func<MemoryRepairOptions, MemoryRepairOptions> change) => RunAsync(
        async () =>
        {
            var current = _options ?? await Service.GetOptionsAsync(Cancel);
            await Service.SaveOptionsAsync(change(current), Cancel);
        },
        "The repair settings could not be saved.");

    private Task RecheckAsync() => RunAsync(() => Service.RecheckAsync(Workspace, Cancel), "The check could not be started.");

    private Task UndoAsync(MemoryRepair item) => RunAsync(() => Service.UndoAsync(Workspace, item.Id, Cancel), "The fix could not be undone.");

    private async Task RunAsync(Func<Task> action, string failure)
    {
        _busy = true;
        try
        {
            await action();
            await LoadAsync();
        }
        catch (InvalidOperationException ex)
        {
            // A refusal the owner can act on (the flow changed since the fix).
            _error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "A memory repair action failed in workspace {Workspace}", Workspace.Name);
            _error = failure;
        }
        finally
        {
            _busy = false;
        }
    }

    private static string ModeHelp(MemoryRepairMode mode) => mode switch
    {
        MemoryRepairMode.Automatic => "Agents are told you approved repairs: they fix drift in code they work on, and the rest before they finish, without asking you.",
        MemoryRepairMode.Ask => "Agents are told about drift and offer to repair it; they repair after you say yes.",
        _ => "Agents are not asked to repair memory; the list is yours.",
    };

    private static string Target(MemoryRepair item) => item.Target == MemoryRepairTarget.Flow
        ? $"flow '{item.TargetName}' step {item.StepOrdinal + 1} ({item.StepSpec})"
        : $"note #{item.TargetId} '{item.TargetName}'";

    private string BranchText() => _counts?.Branch is { } branch
        ? $"checked against {branch}{(_counts.FullScanUtc is { } at ? $", {Waymark.Host.Ui.DisplayFormat.Ago(at, Time.GetUtcNow())}" : string.Empty)}"
        : "not a git checkout, or not checked yet";
}
