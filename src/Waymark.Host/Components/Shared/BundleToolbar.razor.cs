using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Waymark.Core.Transfer;
using Waymark.Host.Transfer;

namespace Waymark.Host.Components.Shared;

/// <summary>
/// The Export and Import buttons of a page that holds what moves to another computer: all workspaces on the Workspaces page, or one workspace's access rules or feature maps
/// on its own page. Export downloads a file; Import opens the dialog that shows what a file adds before anything is saved.
/// </summary>
public partial class BundleToolbar : LiveComponentBase
{
    private bool _busy;
    private string? _error;

    /// <summary>The workspace this page is about; null on the Workspaces page, which exports every workspace.</summary>
    [Parameter]
    public long? WorkspaceId { get; set; }

    /// <summary>What this page's export carries and what its import applies.</summary>
    [Parameter]
    public BundleContent Content { get; set; } = BundleContent.All;

    [Parameter]
    public string ExportLabel { get; set; } = "Export";

    [Parameter]
    public string? Hint { get; set; }

    [Inject]
    private WorkspaceBundleService Bundles { get; set; } = default!;

    [Inject]
    private IJSRuntime Js { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private ILogger<BundleToolbar> Logger { get; set; } = default!;

    private async Task ExportAsync()
    {
        _busy = true;
        _error = null;
        try
        {
            var file = await Bundles.ExportAsync(WorkspaceId, Content, Cancel);
            await Js.InvokeVoidAsync("waymark.download", Cancel, file.FileName, file.Text, "application/json");
            Snackbar.Add($"Saved {file.FileName}. It lists your access rules and feature maps, so keep it somewhere you trust.", Severity.Success);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            Logger.LogDebug(ex, "The bundle download was not delivered");
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            _error = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The bundle could not be exported");
            _error = "The export failed.";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ImportAsync()
    {
        _error = null;
        var parameters = new DialogParameters<ImportBundleDialog>
        {
            { dialog => dialog.TargetWorkspaceId, WorkspaceId },
            { dialog => dialog.Content, Content },
        };
        var options = new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Large, FullWidth = true };
        await Dialogs.ShowAsync<ImportBundleDialog>("Import from a file", parameters, options);
    }
}
