using Microsoft.AspNetCore.Components;
using MudBlazor;
using Waymark.Core.Commands;
using Waymark.Core.Events;
using Waymark.Core.Workspaces;
using Waymark.Host.Commands;
using Waymark.Host.Components.Shared;
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

    private IReadOnlyList<FamilyGroup> Families =>
    [
        Group(CommandCatalog.DotnetFamily, ".NET"),
        Group(CommandCatalog.PythonFamily, "Python"),
        Group(CommandCatalog.NodeFamily, "Node.js"),
        Group(CommandCatalog.GitFamily, "Git"),
        Group(CommandCatalog.ProcessFamily, "Processes and ports"),
        Group(CommandCatalog.ShellFamily, "System shell")
    ];

    private long? ResolveScope => _scope == CommandCatalog.AllWorkspaces ? null : _scope;

    private string ScopeName => _scope == CommandCatalog.AllWorkspaces
        ? "all workspaces"
        : _workspaces.FirstOrDefault(w => w.Id == _scope)?.Name ?? "this workspace";

    private string ScopeHelp => _scope == CommandCatalog.AllWorkspaces
        ? "A choice here applies to every workspace that has no choice of its own."
        : $"A choice here applies to {ScopeName} only and wins over the choice for all workspaces.";

    private int OwnCount => _settings.Count(s => s.WorkspaceId == _scope);

    private static FamilyGroup Group(string family, string title) =>
        new(family, title, CommandCatalog.Operations.Where(o => o.Family == family).ToList());

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
