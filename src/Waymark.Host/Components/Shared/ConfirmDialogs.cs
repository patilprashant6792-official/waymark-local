using MudBlazor;

namespace Waymark.Host.Components.Shared;

/// <summary>Asks the owner to confirm a change. Cancel comes first and is the focused button, and the question names the action.</summary>
public static class ConfirmDialogs
{
    public static async Task<bool> AskAsync(
        IDialogService dialogs,
        string title,
        string message,
        string confirmText,
        string? detail = null,
        bool destructive = false)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        var parameters = new DialogParameters<ConfirmDialog>
        {
            { x => x.Message, message },
            { x => x.Detail, detail },
            { x => x.ConfirmText, confirmText },
            { x => x.Destructive, destructive }
        };
        var options = new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small, FullWidth = true };
        var dialog = await dialogs.ShowAsync<ConfirmDialog>(title, parameters, options);
        var result = await dialog.Result;
        return result is { Canceled: false };
    }
}
