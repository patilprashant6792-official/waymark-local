using Microsoft.AspNetCore.Components;
using MudBlazor;
using Waymark.Core.Approvals;
using Waymark.Core.Auth;
using Waymark.Core.Events;
using Waymark.Host.Auth;
using Waymark.Host.Components.Shared;
using Waymark.Host.Ui;

namespace Waymark.Host.Components.Pages;

public partial class Approvals : LiveComponentBase
{
    private const int HistoryLimit = 20;

    private IReadOnlyList<Approval> _pending = [];
    private IReadOnlyList<Approval> _history = [];
    private IReadOnlyList<OAuthClient> _clients = [];
    private string? _error;
    private bool _busy;

    [Inject]
    private IApprovalStore Store { get; set; } = default!;

    [Inject]
    private ApprovalService Decisions { get; set; } = default!;

    [Inject]
    private IOAuthClientStore ClientStore { get; set; } = default!;

    [Inject]
    private ClientService ClientActions { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private ILogger<Approvals> Logger { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        RefreshOn<ApprovalRequested>(LoadAsync);
        RefreshOn<ApprovalDecided>(LoadAsync);
        RefreshOn<CredentialsRevoked>(LoadAsync);
        _ = TickAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var now = Time.GetUtcNow();
            _pending = await Store.ListPendingAsync(now, Cancel);
            _history = (await Store.ListRecentAsync(HistoryLimit + _pending.Count, Cancel))
                .Where(a => a.Status != ApprovalStatus.Pending || a.ExpiresUtc <= now)
                .Take(HistoryLimit)
                .ToList();
            _clients = await ClientStore.ListAsync(Cancel);
            _error = null;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The Approvals page could not load its data");
            _error = "This page could not be loaded. It will try again in a few seconds.";
        }
    }

    private async Task DecideAsync(Approval approval, bool approve)
    {
        _busy = true;
        try
        {
            var decided = await Decisions.DecideAsync(approval.Id, approve, Cancel);
            if (!decided)
            {
                Snackbar.Add("That request was no longer waiting.", Severity.Warning);
            }

            await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RevokeAsync(OAuthClient client)
    {
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Disconnect this app?",
            $"Disconnect {client.ClientName}? It loses access at once and has to be approved again to come back.",
            "Disconnect",
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        _busy = true;
        try
        {
            var revoked = await ClientActions.RevokeAsync(client.ClientId, Cancel);
            Snackbar.Add(revoked ? $"{client.ClientName} was disconnected." : "That app was already disconnected.", revoked ? Severity.Success : Severity.Info);
            await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        finally
        {
            _busy = false;
        }
    }

    private bool HasDisconnected => _clients.Any(c => c.IsRevoked);

    private bool HasClearableHistory => _history.Any(a => !KeepsActiveApp(a));

    /// <summary>An approval is the record of why an app is connected, so it stays while that app is active.</summary>
    private bool KeepsActiveApp(Approval approval) =>
        approval.Status == ApprovalStatus.Approved
        && _clients.Any(c => c.ClientId == approval.ClientId && !c.IsRevoked);

    private Task ClearDisconnectedAsync() => ClearAsync(
        "Clear disconnected apps?",
        "Remove every disconnected app from this list, together with its past requests and stored tokens? Apps that are still active are not touched.",
        "This cannot be undone. An app that comes back has to be approved again.",
        ClientActions.ClearDisconnectedAsync,
        removed => removed == 1 ? "1 disconnected app was cleared." : $"{removed} disconnected apps were cleared.");

    private Task ClearHistoryAsync() => ClearAsync(
        "Clear recent decisions?",
        "Remove every finished request from this list, not only the ones shown? Requests that are waiting, and approvals of apps that are still connected, are kept.",
        "This cannot be undone. The audit log keeps its own record of each decision.",
        Decisions.ClearHistoryAsync,
        removed => removed == 1 ? "1 decision was cleared." : $"{removed} decisions were cleared.");

    private async Task ClearAsync(string title, string message, string detail, Func<CancellationToken, Task<int>> clear, Func<int, string> done)
    {
        var confirmed = await ConfirmDialogs.AskAsync(Dialogs, title, message, "Clear", detail, destructive: true);
        if (!confirmed)
        {
            return;
        }

        _busy = true;
        try
        {
            var removed = await clear(Cancel);
            Snackbar.Add(removed == 0 ? "There was nothing to clear." : done(removed), removed == 0 ? Severity.Info : Severity.Success);
            await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Clearing the Approvals list failed");
            Snackbar.Add("The list could not be cleared. Nothing was changed.", Severity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private string SecondsLeft(Approval approval)
    {
        var seconds = (int)Math.Max(0, (approval.ExpiresUtc - Time.GetUtcNow()).TotalSeconds);
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    private static (StatusState State, string Text) HistoryState(Approval approval) => approval.Status switch
    {
        ApprovalStatus.Approved => (StatusState.Healthy, "Approved"),
        ApprovalStatus.Denied => (StatusState.Failed, "Denied"),
        _ => (StatusState.Off, "Expired")
    };

    private async Task TickAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(Cancel))
            {
                if (_pending.Count > 0)
                {
                    var expired = _pending.Any(a => a.ExpiresUtc <= Time.GetUtcNow());
                    await InvokeAsync(async () =>
                    {
                        if (expired)
                        {
                            await LoadAsync();
                        }

                        StateHasChanged();
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (ObjectDisposedException)
        {
            // The circuit ended.
        }
    }
}
