using Microsoft.AspNetCore.Components;
using MudBlazor;
using Waymark.Core.Commands;
using Waymark.Core.Events;
using Waymark.Core.Workspaces;
using Waymark.Host.Commands;
using Waymark.Host.Components.Shared;
using Waymark.Host.Ui;
using Waymark.Modules.Commands;
using Waymark.Modules.Commands.Execution;

namespace Waymark.Host.Components.Pages;

public partial class CommandSettings : LiveComponentBase
{
    private static readonly CommandGate[] Choices = [CommandGate.Off, CommandGate.Ask, CommandGate.Auto];

    private readonly Dictionary<long, CommandTrust> _trust = [];
    private IReadOnlyList<Workspace> _workspaces = [];
    private IReadOnlyList<CommandGateSetting> _settings = [];
    private long _scope = CommandCatalog.AllWorkspaces;
    private string? _error;
    private bool _busy;

    [Inject]
    private CommandAdminService Admin { get; set; } = default!;

    [Inject]
    private IWorkspaceStore WorkspaceStore { get; set; } = default!;

    [Inject]
    private ShellLocator Shells { get; set; } = default!;

    [Inject]
    private CommandsOptions Options { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private ILogger<CommandSettings> Logger { get; set; } = default!;

    private sealed record FamilyGroup(string Key, string Title, IReadOnlyList<OperationInfo> Operations);

    private string _filter = string.Empty;

    [SupplyParameterFromQuery(Name = "toolchain")]
    public string? ToolchainQuery { get; set; }

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    private string ToolchainKey => Toolchains.Find(ToolchainQuery)?.Key ?? Toolchains.All;

    // One toolchain picked, or a filter typed: the sections open. Everything shown at once: they stay closed, one summary line each.
    private bool ExpandFamilies => ToolchainKey != Toolchains.All || _filter.Trim().Length > 0;

    private IEnumerable<FamilyGroup> VisibleFamilies =>
        Toolchains.Ordered
            .Where(t => ToolchainKey == Toolchains.All || t.Key == ToolchainKey)
            .Select(t => new FamilyGroup(t.Key, t.Title, Matching(Toolchains.OperationsOf(t.Key))))
            .Where(f => f.Operations.Count > 0)
            .ToList();

    private long? ResolveScope => _scope == CommandCatalog.AllWorkspaces ? null : _scope;

    private string ScopeName => _scope == CommandCatalog.AllWorkspaces
        ? "all workspaces"
        : _workspaces.FirstOrDefault(w => w.Id == _scope)?.Name ?? "this workspace";

    private string ScopeHelp => _scope == CommandCatalog.AllWorkspaces
        ? "A choice here applies to every workspace that has no choice of its own."
        : $"A choice here applies to {ScopeName} only and wins over the choice for all workspaces.";

    private int OwnCount => _settings.Count(s => s.WorkspaceId == _scope);

    private IReadOnlyList<OperationInfo> Matching(IReadOnlyList<OperationInfo> operations)
    {
        var text = _filter.Trim();
        return text.Length == 0
            ? operations
            : operations.Where(o => o.Label.Contains(text, StringComparison.OrdinalIgnoreCase)
                || o.Description.Contains(text, StringComparison.OrdinalIgnoreCase)
                || o.Key.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private sealed record GateCounts(int Total, int Ask, int Off, int Auto, int AutoRunsCode);

    /// <summary>What the owner has chosen across one toolchain, for the scope on screen: shown on the closed section so nothing risky hides in it.</summary>
    private GateCounts CountsOf(string family)
    {
        var operations = Toolchains.OperationsOf(family);
        var gates = operations.Select(o => (Operation: o, Gate: Effective(o))).ToList();
        return new GateCounts(
            operations.Count,
            gates.Count(g => g.Gate == CommandGate.Ask),
            gates.Count(g => g.Gate == CommandGate.Off),
            gates.Count(g => g.Gate == CommandGate.Auto),
            gates.Count(g => g.Gate == CommandGate.Auto && RiskTiers.RunsProjectCode(g.Operation)));
    }

    private string ToolchainLabel(Toolchains.Toolchain toolchain)
    {
        var counts = CountsOf(toolchain.Key);
        return counts.Auto > 0 ? $"{toolchain.Title} ({counts.Total}, {counts.Auto} Auto)" : $"{toolchain.Title} ({counts.Total})";
    }

    private string ToolchainHelp => Toolchains.Find(ToolchainKey) is { } toolchain
        ? $"{toolchain.Title}: {toolchain.Hint}. The choices below apply to {ScopeName}."
        : "Every toolchain, closed to one line each. Open one to change its operations, or pick it above to see only that one.";

    private bool FamilyHasOwn(string family) => Toolchains.OperationsOf(family).Any(HasOwn);

    private static string GateClass(CommandGate gate) => gate switch
    {
        CommandGate.Off => "off",
        CommandGate.Ask => "ask",
        _ => "auto"
    };

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        RefreshOn<CommandPolicyChanged>(LoadAsync);
        RefreshOn<WorkspaceChanged>(LoadAsync);
    }

    private CommandGate Effective(OperationInfo operation) => CommandCatalog.Resolve(operation, ResolveScope, _settings);

    private bool HasOwn(OperationInfo operation) =>
        _settings.Any(s => s.WorkspaceId == _scope && s.Operation == operation.Key);

    private string SourceOf(OperationInfo operation)
    {
        if (HasOwn(operation))
        {
            return _scope == CommandCatalog.AllWorkspaces ? "Your choice for all workspaces" : $"Your choice for {ScopeName}";
        }

        if (_scope != CommandCatalog.AllWorkspaces && _settings.Any(s => s.WorkspaceId == CommandCatalog.AllWorkspaces && s.Operation == operation.Key))
        {
            return "Your choice for all workspaces";
        }

        return "Default";
    }

    private async Task LoadAsync()
    {
        try
        {
            _workspaces = (await WorkspaceStore.ListAsync(Cancel)).OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _settings = await Admin.ListGatesAsync(Cancel);
            if (_scope != CommandCatalog.AllWorkspaces && _workspaces.All(w => w.Id != _scope))
            {
                _scope = CommandCatalog.AllWorkspaces;
            }

            _trust.Clear();
            foreach (var workspace in _workspaces)
            {
                if (await Admin.GetTrustAsync(workspace.Id, Cancel) is { } trust)
                {
                    _trust[workspace.Id] = trust;
                }
            }

            _error = null;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The command settings could not be loaded");
            _error = "The command settings could not be loaded. Reload the page to try again.";
        }
    }

    private void OnScopeChanged(ChangeEventArgs args)
    {
        _scope = long.TryParse(args.Value?.ToString(), out var id) ? id : CommandCatalog.AllWorkspaces;
    }

    // The choice lives in the address (/commands?toolchain=node), so the Safety page, approval cards and bookmarks can link straight to a toolchain.
    private void OnToolchainChanged(ChangeEventArgs args)
    {
        var key = Toolchains.Find(args.Value?.ToString())?.Key ?? Toolchains.All;
        Navigation.NavigateTo(key == Toolchains.All ? "commands" : $"commands?toolchain={Uri.EscapeDataString(key)}", replace: true);
    }

    private async Task SetAsync(OperationInfo operation, CommandGate gate)
    {
        if (_busy || gate == Effective(operation) && HasOwn(operation))
        {
            return;
        }

        if (gate == CommandGate.Auto && (operation.Mutating || RiskTiers.RunsProjectCode(operation)))
        {
            var message = RiskTiers.RunsProjectCode(operation)
                ? $"'{operation.Label}' will start code from the project, including code Claude has just written, as soon as Claude asks for it in {ScopeName}. That code runs with your user rights: it can read your keys and reach the internet."
                : $"'{operation.Label}' will run as soon as Claude asks for it in {ScopeName}, without waiting for you.";
            var confirmed = await ConfirmDialogs.AskAsync(
                Dialogs,
                "Run without asking?",
                message,
                "Allow automatically",
                operation.Description,
                destructive: true);
            if (!confirmed)
            {
                return;
            }
        }

        await RunAsync(() => Admin.SetGateAsync(_scope, ScopeName, operation.Key, gate, Cancel), $"{operation.Label}: {gate}");
    }

    private Task ResetAsync(OperationInfo operation) =>
        RunAsync(() => Admin.ClearGateAsync(_scope, ScopeName, operation.Key, Cancel), $"{operation.Label} follows the next level again");

    /// <summary>Sets every operation of a toolchain that allows the choice, for the scope on screen. Only Off and Ask are offered in bulk: a choice that runs code unasked is made one operation at a time, with its warning.</summary>
    private Task SetFamilyAsync(string family, CommandGate gate) =>
        RunAsync(
            async () =>
            {
                foreach (var operation in Toolchains.OperationsOf(family).Where(o => gate <= o.MaxGate && !(Effective(o) == gate && HasOwn(o))))
                {
                    await Admin.SetGateAsync(_scope, ScopeName, operation.Key, gate, Cancel);
                }
            },
            $"{Toolchains.TitleOf(family)}: all {gate}");

    private Task ResetFamilyAsync(string family) =>
        RunAsync(
            async () =>
            {
                foreach (var operation in Toolchains.OperationsOf(family).Where(HasOwn))
                {
                    await Admin.ClearGateAsync(_scope, ScopeName, operation.Key, Cancel);
                }
            },
            $"{Toolchains.TitleOf(family)} follows the defaults again");

    private async Task ResetScopeAsync()
    {
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Reset these choices?",
            $"{OwnCount} {(OwnCount == 1 ? "choice" : "choices")} for {ScopeName} will be removed. The operations follow their defaults again.",
            "Reset");
        if (confirmed)
        {
            await RunAsync(() => Admin.ClearScopeAsync(_scope, ScopeName, Cancel), "Choices reset");
        }
    }

    private async Task ForgetAsync(Workspace workspace)
    {
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Forget the trusted project files?",
            $"The next command in {workspace.Name} that loads project files will ask for your approval, even if you set it to Auto.",
            "Forget");
        if (confirmed)
        {
            await RunAsync(() => Admin.ForgetTrustAsync(workspace.Id, workspace.Name, Cancel), "Project files are no longer trusted");
        }
    }

    private async Task RunAsync(Func<Task> change, string done)
    {
        _busy = true;
        _error = null;
        try
        {
            await change();
            await LoadAsync();
            Snackbar.Add(done, Severity.Success);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (ArgumentException ex)
        {
            _error = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "A command setting could not be saved");
            _error = "That change could not be saved. Nothing was changed.";
        }
        finally
        {
            _busy = false;
        }
    }
}
