using Microsoft.AspNetCore.Components;
using MudBlazor;
using Waymark.Core.Workspaces;
using Waymark.Modules.Code.Scheduling;

namespace Waymark.Host.Components.Shared;

/// <summary>The form that adds a workspace: check the folder, name it, add it. Closes itself when the workspace was added.</summary>
public partial class AddWorkspaceDialog : LiveComponentBase
{
    private WorkspaceValidation? _check;
    private string _name = string.Empty;
    private string _path = string.Empty;
    private string? _error;
    private bool _busy;

    [CascadingParameter]
    private IMudDialogInstance Dialog { get; set; } = default!;

    [Inject]
    private IWorkspaceService Service { get; set; } = default!;

    [Inject]
    private IIndexScheduler Scheduler { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private ILogger<AddWorkspaceDialog> Logger { get; set; } = default!;

    private bool CanAdd => !_busy && !string.IsNullOrWhiteSpace(_path) && !string.IsNullOrWhiteSpace(_name);

    private async Task CheckAsync()
    {
        _error = null;
        try
        {
            _check = await Service.ValidateAsync(_path, Cancel);
            if (_check is { IsValid: true, NormalizedPath: { } folder } && string.IsNullOrWhiteSpace(_name))
            {
                // Offer the folder's own name when it is an acceptable workspace name.
                var suggestion = Path.GetFileName(folder);
                if (WorkspacePathRules.ValidateName(suggestion) is null)
                {
                    _name = suggestion;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The folder could not be checked");
            _error = "The folder could not be checked.";
        }
    }

    private async Task AddAsync()
    {
        if (!CanAdd)
        {
            return;
        }

        _error = null;
        _busy = true;
        try
        {
            var result = await Service.AddAsync(_name, _path, Cancel);
            if (!result.IsSuccess || result.Workspace is null)
            {
                _error = result.Error ?? "The workspace could not be added.";
                return;
            }

            Scheduler.RequestReconcile(result.Workspace.Id);
            Snackbar.Add($"Added '{result.Workspace.Name}'. Indexing has started.", Severity.Success);
            Dialog.Close(DialogResult.Ok(result.Workspace.Id));
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The workspace could not be added");
            _error = "The workspace could not be added.";
        }
        finally
        {
            _busy = false;
        }
    }

    private void Close() => Dialog.Cancel();
}
