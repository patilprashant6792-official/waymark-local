using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Waymark.Core.Auth;
using Waymark.Core.Edge;
using Waymark.Core.Events;
using Waymark.Core.Workspaces;
using Waymark.Host.Components.Shared;
using Waymark.Host.Edge;

namespace Waymark.Host.Components.Pages;

public partial class Setup : LiveComponentBase
{
    private string _address = string.Empty;
    private string _authtoken = string.Empty;
    private NgrokAuthInfo _authInfo = new(false, null);
    private PublicUrlCheck? _check;
    private int _workspaceCount;
    private bool _verified;
    private bool _busy;
    private string? _error;

    [Inject]
    private PublicUrlService Urls { get; set; } = default!;

    [Inject]
    private IPublicUrlProvider PublicUrl { get; set; } = default!;

    [Inject]
    private INgrokAuthStore AuthStore { get; set; } = default!;

    [Inject]
    private NgrokTunnel Tunnel { get; set; } = default!;

    [Inject]
    private IWorkspaceService WorkspaceService { get; set; } = default!;

    [Inject]
    private EdgeOptions Edge { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private IJSRuntime Js { get; set; } = default!;

    [Inject]
    private ILogger<Setup> Logger { get; set; } = default!;

    private string? SavedUrl => PublicUrl.Current;

    private string? ConnectorUrl => SavedUrl is null ? null : SavedUrl + "/mcp";

    private string TunnelCommand => $"ngrok http {Edge.McpPort}";

    private TunnelStatus TunnelNow => Tunnel.Status;

    private bool ConnectorUnlocked => _verified && ConnectorUrl is not null;

    private bool HasFailure => _check is { Success: false } || TunnelNow.Phase == TunnelPhase.Failed;

    protected override async Task OnInitializedAsync()
    {
        _address = SavedUrl ?? string.Empty;
        await LoadAsync();
        RefreshOn<TunnelStatusChanged>(() => Task.CompletedTask);
        RefreshOn<PublicUrlChanged>(LoadAsync);
        RefreshOn<WorkspaceChanged>(LoadAsync);
    }

    private async Task LoadAsync()
    {
        try
        {
            _authInfo = await AuthStore.GetInfoAsync(Cancel);
            _workspaceCount = (await WorkspaceService.ListAsync(Cancel)).Count;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The setup state could not be loaded");
            _error = "The setup state could not be loaded.";
        }
    }

    private async Task SaveAsync()
    {
        await RunAsync(async () =>
        {
            _check = await Urls.VerifyAndSaveAsync(_address, EmptyToNull(_authtoken), AuthDefaults.AdminActor, Cancel);
            _verified = _check.Success;
            _authtoken = string.Empty;
            await LoadAsync();
        });
    }

    private async Task CheckAsync()
    {
        await RunAsync(async () =>
        {
            _check = await Urls.VerifyCurrentAsync(Cancel);
            _verified = _check.Success;
        });
    }

    private async Task StopAsync()
    {
        await RunAsync(async () =>
        {
            await Urls.StopTunnelAsync(AuthDefaults.AdminActor, Cancel);
            _verified = false;
            _check = null;
        });
    }

    private async Task DisconnectAsync()
    {
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Disconnect the public address?",
            "The tunnel stops and the saved address is forgotten. Claude can no longer reach this computer until you set it up again. Your ngrok authtoken is kept.",
            "Disconnect",
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await Urls.DisconnectAsync(AuthDefaults.AdminActor, Cancel);
            _address = string.Empty;
            _verified = false;
            _check = null;
        });
    }

    private async Task CopyAsync(string text)
    {
        try
        {
            await Js.InvokeVoidAsync("waymark.copy", Cancel, text);
            Snackbar.Add("Copied.", Severity.Success);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException)
        {
            Snackbar.Add("Copying was blocked. Select the text and copy it by hand.", Severity.Warning);
        }
    }

    private async Task RunAsync(Func<Task> work)
    {
        _error = null;
        _busy = true;
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "A setup step failed");
            _error = "That step failed unexpectedly. The log has the details.";
        }
        finally
        {
            _busy = false;
        }
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private (StatusState State, string Text) TunnelChip() => TunnelNow.Phase switch
    {
        TunnelPhase.Online => (StatusState.Healthy, "Tunnel online"),
        TunnelPhase.Installing or TunnelPhase.Starting => (StatusState.Working, "Starting"),
        TunnelPhase.Failed => (StatusState.Failed, "Tunnel failed"),
        _ => (StatusState.Off, "Tunnel stopped")
    };
}
