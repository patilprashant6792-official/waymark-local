using Microsoft.AspNetCore.Components;
using MudBlazor;
using Waymark.Core.Vault;
using Waymark.Host.Components.Shared;
using Waymark.Host.Vault;

namespace Waymark.Host.Components.Pages;

public partial class Vault : LiveComponentBase
{
    private IReadOnlyList<VaultSecret> _secrets = [];
    private string _name = string.Empty;
    private string _value = string.Empty;
    private string? _error;
    private bool _busy;

    [Inject]
    private VaultService Service { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private ILogger<Vault> Logger { get; set; } = default!;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            _secrets = await Service.ListAsync(Cancel);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The vault could not be loaded");
            _error = "The vault could not be loaded.";
        }
    }

    private async Task SaveAsync()
    {
        _error = null;
        _busy = true;
        try
        {
            var created = await Service.SaveAsync(_name, _value, Cancel);
            Snackbar.Add(created ? "The secret was saved." : "The secret was replaced.", Severity.Success);
            _name = string.Empty;
            _value = string.Empty;
            await LoadAsync();
        }
        catch (ArgumentException ex)
        {
            _error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The secret could not be saved");
            _error = "The secret could not be saved.";
        }
        finally
        {
            _busy = false;
        }
    }

    private void Replace(VaultSecret secret)
    {
        _name = secret.Name;
        _value = string.Empty;
    }

    private async Task DeleteAsync(VaultSecret secret)
    {
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Delete this secret?",
            $"'{secret.Name}' is deleted for good. Anything that refers to it stops working.",
            "Delete",
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        try
        {
            await Service.DeleteAsync(secret.Name, Cancel);
            await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
    }
}
