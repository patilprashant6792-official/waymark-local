using Microsoft.AspNetCore.Components;
using MudBlazor;
using Waymark.Core.Auth;
using Waymark.Core.Events;
using Waymark.Host.Auth;
using Waymark.Host.Components.Shared;

namespace Waymark.Host.Components.Pages;

public partial class AccessKeys : LiveComponentBase
{
    private const int NoExpiry = 0;

    private IReadOnlyList<AccessKey> _keys = [];
    private CreatedAccessKey? _created;
    private string _name = string.Empty;
    private int _lifetimeDays = (int)AuthDefaults.ApiKeyDefaultLifetime.TotalDays;
    private string? _error;
    private bool _busy;

    [Inject]
    private IAccessKeyStore Store { get; set; } = default!;

    [Inject]
    private AccessKeyService Service { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private ILogger<AccessKeys> Logger { get; set; } = default!;

    private static IReadOnlyList<(int Days, string Text)> Lifetimes { get; } =
    [
        (30, "30 days"),
        (90, "90 days"),
        (365, "1 year"),
        (NoExpiry, "Never expires")
    ];

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        RefreshOn<CredentialsRevoked>(LoadAsync);
    }

    private async Task LoadAsync()
    {
        try
        {
            _keys = await Store.ListAsync(Cancel);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The access keys could not be loaded");
            _error = "The keys could not be loaded.";
        }
    }

    private async Task CreateAsync()
    {
        _error = null;
        _busy = true;
        try
        {
            TimeSpan? lifetime = _lifetimeDays == NoExpiry ? null : TimeSpan.FromDays(_lifetimeDays);
            _created = await Service.CreateAsync(_name, lifetime, Cancel);
            _name = string.Empty;
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
        finally
        {
            _busy = false;
        }
    }

    private async Task RevokeAsync(AccessKey key)
    {
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Revoke this key?",
            $"Revoke the key '{key.Name}' (ends in {key.Hint})? Anything that uses it stops working at once.",
            "Revoke",
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        _busy = true;
        try
        {
            var revoked = await Service.RevokeAsync(key.Id, Cancel);
            Snackbar.Add(revoked ? "The key was revoked." : "That key was already revoked.", revoked ? Severity.Success : Severity.Info);
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

    private (StatusState State, string Text) StateOf(AccessKey key)
    {
        if (key.IsRevoked)
        {
            return (StatusState.Off, "Revoked");
        }

        return key.IsExpired(Time.GetUtcNow()) ? (StatusState.Attention, "Expired") : (StatusState.Healthy, "Active");
    }
}
