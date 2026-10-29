using Microsoft.AspNetCore.Components;
using MudBlazor;
using Waymark.Core.Events;
using Waymark.Core.NuGetIndex;
using Waymark.Host.Components.Shared;
using Waymark.Host.NuGetAdmin;

namespace Waymark.Host.Components.Pages;

public partial class NuGetCache : LiveComponentBase
{
    private NuGetCacheOverview? _overview;
    private string _filter = string.Empty;
    private string? _error;
    private string? _status;
    private long _workingId = -1;
    private bool _busy;

    [Inject]
    private NuGetCacheService Service { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private ILogger<NuGetCache> Logger { get; set; } = default!;

    // Narrow the list by ecosystem (nuget, pypi, npm; empty is all) and by a part of the package name.
    private string _ecosystem = string.Empty;

    private IReadOnlyList<NuGetCacheEntry> Visible =>
        _overview is null
            ? []
            : _overview.Entries
                .Where(entry => _ecosystem.Length == 0 || PackageEcosystems.Of(entry.Package.Key) == _ecosystem)
                .Where(entry => string.IsNullOrWhiteSpace(_filter) || entry.Package.Key.PackageId.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

    private int CountOf(string ecosystem) =>
        _overview?.Entries.Count(entry => PackageEcosystems.Of(entry.Package.Key) == ecosystem) ?? 0;

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        RefreshOn<NuGetCacheChanged>(LoadAsync);
    }

    private async Task LoadAsync()
    {
        try
        {
            _overview = await Service.GetOverviewAsync(Cancel);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The package overview could not be loaded");
            _error = "The packages could not be loaded.";
        }
    }

    private (StatusState State, string Text) StateOf(NuGetCacheEntry entry)
    {
        if (_workingId == entry.Package.Id)
        {
            return (StatusState.Working, "Reloading");
        }

        return entry.Package.State switch
        {
            NuGetIndexState.Ready => (StatusState.Healthy, "Ready"),
            NuGetIndexState.Failed => (StatusState.Failed, "Failed"),
            _ => (StatusState.Working, "Building"),
        };
    }

    private async Task ReloadAsync(NuGetCacheEntry entry)
    {
        var package = entry.Package;
        _error = null;
        _busy = true;
        _workingId = package.Id;
        _status = $"Reloading {package.Key.PackageId} {package.Key.Version}. This downloads and reads the package again and can take a minute.";
        try
        {
            var failure = await Service.ReloadAsync(entry);
            if (failure is null)
            {
                Snackbar.Add($"{package.Key.PackageId} {package.Key.Version} was reloaded.", Severity.Success);
            }
            else
            {
                _error = $"{package.Key.PackageId} {package.Key.Version} could not be reloaded: {failure}";
            }
        }
        finally
        {
            _workingId = -1;
            _busy = false;
            _status = null;
            await LoadAsync();
        }
    }

    private async Task ReloadFailedAsync()
    {
        if (_overview is null)
        {
            return;
        }

        var failed = _overview.Entries.Where(entry => entry.Package.State == NuGetIndexState.Failed).ToList();
        _error = null;
        _busy = true;
        _status = $"Reloading {failed.Count} failed packages one after the other. This can take several minutes.";
        try
        {
            var (reloaded, stillFailing) = await Service.ReloadAllAsync(failed);
            if (stillFailing == 0)
            {
                Snackbar.Add($"{reloaded} packages were reloaded.", Severity.Success);
            }
            else
            {
                _error = $"{reloaded} packages were reloaded and {stillFailing} still fail. The reason is shown on each row.";
            }
        }
        finally
        {
            _busy = false;
            _status = null;
            await LoadAsync();
        }
    }

    private async Task ClearAsync(NuGetCacheEntry entry)
    {
        var key = entry.Package.Key;
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Clear this package?",
                $"The index and the stored results of {key.PackageId} {key.Version} ({PackageEcosystems.Of(key)}) are deleted. It is downloaded and read again the next time Claude asks for it. NuGet package files in the shared NuGet folder stay.",
            "Clear",
            destructive: true);
        if (confirmed)
        {
            await RunClearAsync(() => Service.ClearAsync(key, Cancel));
        }
    }

    private async Task ClearFailedAsync()
    {
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Clear the failed packages?",
            "Every package that failed to index is removed, with its stored results. Packages that are ready are not touched.",
            "Clear failed",
            destructive: true);
        if (confirmed)
        {
            await RunClearAsync(() => Service.ClearFailedAsync(Cancel));
        }
    }

    private async Task ClearAllAsync()
    {
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Clear everything?",
                    "All package indexes and stored results (NuGet, PyPI and npm) are deleted. Each package is downloaded and read again the next time Claude asks for it, which is slow for large packages. NuGet package files in the shared NuGet folder stay.",
            "Clear everything",
            destructive: true);
        if (confirmed)
        {
            await RunClearAsync(() => Service.ClearAllAsync(Cancel));
        }
    }

    private async Task RemoveResultAsync(NuGetCachedResult result)
    {
        _error = null;
        _busy = true;
        try
        {
            await Service.RemoveCachedResultAsync(result.CacheKey, Cancel);
            await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The stored result could not be removed");
            _error = "The stored result could not be removed.";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RunClearAsync(Func<Task<NuGetClearResult>> clear)
    {
        _error = null;
        _busy = true;
        try
        {
            var result = await clear();
            Snackbar.Add($"Cleared {result.Packages} packages and {result.CachedResults} stored results.", Severity.Success);
            await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The NuGet data could not be cleared");
            _error = "The NuGet data could not be cleared.";
        }
        finally
        {
            _busy = false;
        }
    }
}
