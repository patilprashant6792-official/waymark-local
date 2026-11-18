using Microsoft.AspNetCore.Components;
using MudBlazor;
using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Events;
using Waymark.Core.Guides;
using Waymark.Core.Tools;
using Waymark.Core.Workspaces;
using Waymark.Host.Components.Shared;
using Waymark.Host.Mcp;
using Waymark.Host.Ui;

namespace Waymark.Host.Components.Pages;

/// <summary>
/// The Instructions section of a workspace: the owner's one-line summary (listed in the overview) and instructions (only in the workspace's guide),
/// who holds the current guide, and the guide as an agent receives it. The preview reloads when a flow of the workspace changes.
/// </summary>
public partial class WorkspaceInstructionsEditor : LiveComponentBase
{
    private const string Placeholder =
        "For example:\n\n" +
        "## Layout\n- Endpoints in Api/, business logic in Core/Services, data access in Data/. New code follows the same split.\n\n" +
        "## Before you finish\n- Build the solution and run the tests of the project you changed.\n\n" +
        "## Always ask first\n- Before adding a migration, a package or a public endpoint.";

    private WorkspaceGuideText _saved = WorkspaceGuideText.Empty;
    private IReadOnlyList<GuideDelivery> _deliveries = [];
    private ToolGuide? _guide;
    private string _summaryDraft = string.Empty;
    private string _draft = string.Empty;
    private int _draftTokens;
    private int _guideTokens;
    private string _preview = string.Empty;
    private long _shownId;
    private bool _showPreview;
    private bool _loaded;
    private string? _error;
    private bool _busy;

    [Parameter]
    [EditorRequired]
    public Workspace Workspace { get; set; } = default!;

    [Inject]
    private IWorkspaceGuides Guides { get; set; } = default!;

    [Inject]
    private WorkspaceGuideRenderer Renderer { get; set; } = default!;

    [Inject]
    private IGuideStore Store { get; set; } = default!;

    [Inject]
    private IContextMeter Meter { get; set; } = default!;

    [Inject]
    private IAuditStore Audit { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private ILogger<WorkspaceInstructionsEditor> Logger { get; set; } = default!;

    private int SummaryChars => WorkspaceGuideRules.NormalizeSummary(_summaryDraft).Length;

    private int DraftChars => GuideRules.Normalize(_draft).Length;

    private bool TooLong => DraftChars > WorkspaceGuideRules.MaxInstructionsChars || SummaryChars > WorkspaceGuideRules.MaxSummaryChars;

    private bool IsDirty =>
        !string.Equals(GuideRules.Normalize(_draft), _saved.Instructions, StringComparison.Ordinal)
        || !string.Equals(WorkspaceGuideRules.NormalizeSummary(_summaryDraft), _saved.Summary, StringComparison.Ordinal);

    private string ModuleKey => WorkspaceGuideRules.ModuleKey(Workspace.Id);

    // A saved flow changes the preview (the live setup), never the text being edited. Text saved elsewhere (an import, another tab) replaces the
    // fields only while they hold no edits of their own, so nothing typed here is lost.
    protected override void OnInitialized()
    {
        RefreshOn<FeatureMapsChanged>(changed => changed.WorkspaceId == Workspace.Id ? LoadAsync() : Task.CompletedTask);
        RefreshOn<WorkspaceGuideChanged>(OnGuideChangedAsync);
    }

    private async Task OnGuideChangedAsync(WorkspaceGuideChanged changed)
    {
        if (changed.WorkspaceId != Workspace.Id)
        {
            return;
        }

        var hadEdits = IsDirty;
        await LoadAsync();
        if (!hadEdits)
        {
            Revert();
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_shownId == Workspace.Id)
        {
            return;
        }

        _shownId = Workspace.Id;
        _loaded = false;
        _error = null;
        await LoadAsync();
        Revert();
        _loaded = true;
    }

    private string LastChange() =>
        _saved.ChangedUtc is { } at ? $"Saved {DisplayFormat.Ago(at, Time.GetUtcNow())}" : "Nothing saved yet";

    private string HolderText()
    {
        if (_guide is null)
        {
            return string.Empty;
        }

        var current = _deliveries.Count(d => d.Module == ModuleKey && d.ContentHash == _guide.Hash);
        var older = _deliveries.Count(d => d.Module == ModuleKey && d.ContentHash != _guide.Hash);
        if (current + older == 0)
        {
            return "No agent has loaded this workspace's guide yet.";
        }

        return older == 0
            ? $"{Clients(current)} {(current == 1 ? "holds" : "hold")} the current guide."
            : $"{Clients(current)} {(current == 1 ? "holds" : "hold")} the current guide; {older} {(older == 1 ? "holds" : "hold")} an older one and {(older == 1 ? "is" : "are")} told on their next call here.";
    }

    private static string Clients(int count) => count == 1 ? "1 client" : $"{count} clients";

    private void OnSummaryInput(string? value) => _summaryDraft = value ?? string.Empty;

    private void OnInput(string? value)
    {
        _draft = value ?? string.Empty;
        _draftTokens = Meter.CountTokens(_draft);
    }

    private void Revert()
    {
        _summaryDraft = _saved.Summary;
        _draft = _saved.Instructions;
        _draftTokens = Meter.CountTokens(_draft);
    }

    private async Task LoadAsync()
    {
        try
        {
            _saved = await Guides.GetAsync(Workspace.Id, Cancel);
            _deliveries = await Store.ListDeliveriesAsync(Cancel);
            _guide = await Renderer.RenderAsync(Workspace, Cancel);
            _preview = GuideRules.DeliverWorkspace(Workspace.Name, _guide.Content);
            _guideTokens = Meter.CountTokens(_preview);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The instructions of workspace {Workspace} could not be loaded", Workspace.Name);
            _error = "The instructions of this workspace could not be loaded.";
        }
    }

    private async Task SaveAsync()
    {
        _error = null;
        _busy = true;
        try
        {
            await Guides.SaveAsync(Workspace.Id, _summaryDraft, _draft, AuthDefaults.AdminActor, Cancel);
            await Audit.AppendAsync(AuditEntries.Create(Time, AuditCategory.Config, "config.workspaceprompt.save", AuthDefaults.AdminActor, Workspace.Name), Cancel);
            await LoadAsync();
            Revert();
            Snackbar.Add("Saved. Agents that hold the old text are told on their next call in this workspace.", Severity.Success);
        }
        catch (ArgumentException ex)
        {
            _error = ex.Message;
        }
        catch (KeyNotFoundException ex)
        {
            _error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The instructions of workspace {Workspace} could not be saved", Workspace.Name);
            _error = "Your instructions could not be saved.";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ClearAsync()
    {
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Clear the summary and instructions?",
            "Both are deleted. This workspace's guide then holds only its flows and gates, and the overview lists the workspace without a summary.",
            "Clear",
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        _error = null;
        _busy = true;
        try
        {
            await Guides.SaveAsync(Workspace.Id, string.Empty, string.Empty, AuthDefaults.AdminActor, Cancel);
            await Audit.AppendAsync(AuditEntries.Create(Time, AuditCategory.Config, "config.workspaceprompt.clear", AuthDefaults.AdminActor, Workspace.Name), Cancel);
            await LoadAsync();
            Revert();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The instructions of workspace {Workspace} could not be cleared", Workspace.Name);
            _error = "Your instructions could not be cleared.";
        }
        finally
        {
            _busy = false;
        }
    }
}
