using Microsoft.AspNetCore.Components;
using Waymark.Core.Audit;
using Waymark.Core.Commands;
using Waymark.Core.Events;
using Waymark.Core.Security;
using Waymark.Core.Tools;
using Waymark.Host.Commands;
using Waymark.Host.Components.Shared;

namespace Waymark.Host.Components.Pages;

public partial class RiskGuide : LiveComponentBase
{
    private const int LedgerRows = 25;

    private IReadOnlyList<CommandGateSetting> _settings = [];
    private IReadOnlyList<StoredAuditEntry> _ledger = [];
    private string? _error;

    [Inject]
    private IAuditStore Audit { get; set; } = default!;

    [Inject]
    private CommandAdminService Admin { get; set; } = default!;

    [Inject]
    private SafetyOptions Options { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private ILogger<RiskGuide> Logger { get; set; } = default!;

    // The ones that start code first, then the ones that send data out, then changes, then reads.
    private static IReadOnlyList<OperationInfo> Operations { get; } = CommandCatalog.Operations
        .OrderBy(operation => RiskTiers.Of(operation) switch
        {
            RiskTier.RunsCode => 0,
            RiskTier.SendsOut => 1,
            RiskTier.Change => 2,
            _ => 3
        })
        .ThenBy(operation => operation.Family, StringComparer.Ordinal)
        .ThenBy(operation => operation.Label, StringComparer.Ordinal)
        .ToList();

    private static int ProjectCodeCount => CommandCatalog.Operations.Count(RiskTiers.RunsProjectCode);

    // Automatic anywhere counts: for all workspaces by default, or one workspace by the owner's own choice.
    private IReadOnlyList<OperationInfo> AutoProjectCode =>
        CommandCatalog.Operations
            .Where(RiskTiers.RunsProjectCode)
            .Where(operation => GateOf(operation) == CommandGate.Auto || _settings.Any(s => s.Operation == operation.Key && s.Gate == CommandGate.Auto))
            .ToList();

    private int AskingProjectCodeCount => ProjectCodeCount - AutoProjectCode.Count;

    private CommandGate GateOf(OperationInfo operation) => CommandCatalog.Resolve(operation, null, _settings);

    private static StatusState GateState(OperationInfo operation, CommandGate gate) => gate switch
    {
        CommandGate.Off => StatusState.Off,
        CommandGate.Ask => StatusState.Healthy,
        _ when RiskTiers.RunsProjectCode(operation) => StatusState.Failed,
        _ => RiskTiers.Of(operation) == RiskTier.Look ? StatusState.Healthy : StatusState.Attention
    };

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        RefreshOn<CommandPolicyChanged>(LoadAsync);
    }

    private async Task LoadAsync()
    {
        try
        {
            _settings = await Admin.ListGatesAsync(Cancel);
            _ledger = await Audit.QueryAsync(new AuditQuery(Category: AuditCategory.Config, Action: ExposureLedger.Action, Limit: LedgerRows), Cancel);
            _error = null;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The safety page could not be loaded");
            _error = "Some of this page could not be loaded. Reload the page to try again.";
        }
    }
}
