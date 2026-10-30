using Microsoft.AspNetCore.Components;
using MudBlazor;
using Waymark.Core.Workspaces;
using Waymark.Modules.Code.Scheduling;

namespace Waymark.Host.Components.Shared;

/// <summary>The quick actions of a workspace: switch it on or off, rebuild its index, remove it. Used on the list card and on the details page.</summary>
public partial class WorkspaceActions : LiveComponentBase
{
    private bool _busy;

    [Parameter, EditorRequired]
    public Workspace Workspace { get; set; } = default!;

    /// <summary>Words next to the icons (details page) instead of icons only (card).</summary>
    [Parameter]
    public bool Labels { get; set; }

    /// <summary>Raised after the workspace was removed, so a details page can leave itself.</summary>
    [Parameter]
    public EventCallback OnRemoved { get; set; }

    [Inject]
    private IWorkspaceService Service { get; set; } = default!;

    [Inject]
    private IIndexScheduler Scheduler { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private ILogger<WorkspaceActions> Logger { get; set; } = default!;

    private async Task ToggleAsync(bool enabled)
    {
        _busy = true;
        try
        {
            if (!await Service.SetEnabledAsync(Workspace.Id, enabled, Cancel))
            {
                Snackbar.Add("That workspace no longer exists.", Severity.Warning);
                return;
            }

            if (enabled)
            {
                Scheduler.RequestReconcile(Workspace.Id);
            }
            else
            {
                Scheduler.Forget(Workspace.Id);
            }
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The workspace {Workspace} could not be switched", Workspace.Name);
            Snackbar.Add("The change could not be saved.", Severity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void Reindex()
    {
        Scheduler.RequestRebuild(Workspace.Id);
        Snackbar.Add($"Re-indexing '{Workspace.Name}'.", Severity.Info);
    }

    private async Task RemoveAsync()
    {
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Remove this workspace?",
            $"'{Workspace.Name}' is forgotten and its index is deleted. Your files are not touched.",
            "Remove",
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        try
        {
            Scheduler.Forget(Workspace.Id);
            await Service.RemoveAsync(Workspace.Id, Cancel);
            await OnRemoved.InvokeAsync();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The workspace {Workspace} could not be removed", Workspace.Name);
            Snackbar.Add("The workspace could not be removed.", Severity.Error);
        }
    }
}
