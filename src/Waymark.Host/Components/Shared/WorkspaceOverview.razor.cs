using Microsoft.AspNetCore.Components;
using Waymark.Core.Code;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;
using Waymark.Host.Memory;
using Waymark.Host.Ui;

namespace Waymark.Host.Components.Shared;

/// <summary>
/// The Overview section of a workspace: what is indexed, what its memory holds (flows, notes and when agents last used them), and which projects the folder holds.
/// The memory counts come from <see cref="WorkspaceShell"/> as a cascading value, so they load once and update live with the tabs; the figures that need
/// the code read (notes that may be stale, links that do not call) are shown only when the Notes or Flows page computed them recently.
/// </summary>
public partial class WorkspaceOverview : LiveComponentBase
{
    private CodeIndexStats? _stats;
    private WorkspaceMetadata? _meta;
    private string? _scanError;
    private string? _scannedPath;
    private bool _scanning;

    [Parameter, EditorRequired]
    public Workspace Workspace { get; set; } = default!;

    /// <summary>The memory counts the shell loaded; null until they are there.</summary>
    [CascadingParameter]
    public MemoryCounts? Counts { get; set; }

    [Inject]
    private IWorkspaceService Service { get; set; } = default!;

    [Inject]
    private ICodeIndexStore Index { get; set; } = default!;

    [Inject]
    private MemoryHealthCache Health { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private ILogger<WorkspaceOverview> Logger { get; set; } = default!;

    private MemoryHealth MemoryHealth => Health.Get(Workspace.Id);

    private string FlowsHref => WorkspaceSections.Flows.Href(Workspace.Id);

    private string NotesHref(string? view = null) => view is null ? WorkspaceSections.Notes.Href(Workspace.Id) : $"{WorkspaceSections.Notes.Href(Workspace.Id)}?view={view}";

    private string LastUsedText(DateTimeOffset? used) => DisplayFormat.Ago(used, Time.GetUtcNow());

    private static string ExactTime(DateTimeOffset? used) => used is { } at ? DisplayFormat.DateAndClock(at) : "Never opened by an agent";

    protected override void OnInitialized() => RefreshOn<CodeIndexProgress>(OnProgressAsync);

    protected override async Task OnParametersSetAsync()
    {
        var scan = !string.Equals(_scannedPath, Workspace.Path, StringComparison.Ordinal);
        if (scan)
        {
            _scannedPath = Workspace.Path;
            _scanning = true;
            _meta = null;
            _scanError = null;
        }

        await LoadStatsAsync();
        if (scan)
        {
            await ScanAsync(Workspace.Path);
        }
    }

    private Task OnProgressAsync(CodeIndexProgress progress) =>
        progress.WorkspaceId == Workspace.Id ? LoadStatsAsync() : Task.CompletedTask;

    private async Task LoadStatsAsync()
    {
        try
        {
            _stats = await Index.GetStatsAsync(Workspace.Id, Cancel);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The index figures of workspace {WorkspaceId} could not be loaded", Workspace.Id);
        }
    }

    private async Task ScanAsync(string path)
    {
        try
        {
            var validation = await Service.ValidateAsync(path, Cancel);
            _meta = validation.IsValid ? validation.Metadata : null;
            _scanError = validation.IsValid ? null : validation.Error;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The folder of workspace {WorkspaceId} could not be scanned", Workspace.Id);
            _scanError = "The folder could not be scanned.";
        }
        finally
        {
            _scanning = false;
        }
    }
}
