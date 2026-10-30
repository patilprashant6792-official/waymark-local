using Microsoft.AspNetCore.Components;
using Waymark.Core.Code;
using Waymark.Core.Workspaces;

namespace Waymark.Host.Components.Shared;

/// <summary>The Overview section of a workspace: what is indexed and which projects the folder holds.</summary>
public partial class WorkspaceOverview : LiveComponentBase
{
    private CodeIndexStats? _stats;
    private WorkspaceMetadata? _meta;
    private string? _scanError;
    private string? _scannedPath;
    private bool _scanning;

    [Parameter, EditorRequired]
    public Workspace Workspace { get; set; } = default!;

    [Inject]
    private IWorkspaceService Service { get; set; } = default!;

    [Inject]
    private ICodeIndexStore Index { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private ILogger<WorkspaceOverview> Logger { get; set; } = default!;

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
