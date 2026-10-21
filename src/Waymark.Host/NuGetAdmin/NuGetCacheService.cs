using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Events;
using Waymark.Core.NuGetIndex;

namespace Waymark.Host.NuGetAdmin;

/// <summary>
/// Lets the owner look at, reload and clear what Waymark keeps about NuGet packages. Every change is written to the audit trail.
/// A reload runs to its end even when the page is closed (up to a time limit), so it never leaves a half-built package behind.
/// </summary>
public sealed class NuGetCacheService(
    INuGetCacheStore store,
    INuGetReloader reloader,
    IAuditStore audit,
    IEventBus events,
    TimeProvider time,
    ILogger<NuGetCacheService> logger)
{
    private const int MaxReasonLength = 300;
    private static readonly TimeSpan ReloadTimeout = TimeSpan.FromMinutes(5);

    public Task<NuGetCacheOverview> GetOverviewAsync(CancellationToken ct) => store.GetOverviewAsync(ct);

    /// <summary>Builds the package again from the feed. Null when it worked, otherwise why it did not.</summary>
    public async Task<string?> ReloadAsync(NuGetCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var key = entry.Package.Key;
        string? failure = null;
        using var timeout = new CancellationTokenSource(ReloadTimeout);
        try
        {
            await reloader.ReloadAsync(key, entry.ExtraPackageIds, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            failure = $"Reloading took longer than {ReloadTimeout.TotalMinutes:0} minutes and was stopped.";
        }
        catch (Exception ex)
        {
            // The reason is shown to the owner; the details stay in the log.
            logger.LogWarning(ex, "Reloading {Package} failed", Describe(key));
            failure = Short(ex.Message);
        }

        await AppendAsync(failure is null ? "nuget.reloaded" : "nuget.reload_failed", Describe(key), failure is null ? AuditOutcome.Ok : AuditOutcome.Error, null, failure);
        await events.PublishAsync(new NuGetCacheChanged());
        return failure;
    }

    /// <summary>Reloads the given packages one after the other. Returns how many worked and how many failed.</summary>
    public async Task<(int Reloaded, int Failed)> ReloadAllAsync(IEnumerable<NuGetCacheEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var reloaded = 0;
        var failed = 0;
        foreach (var entry in entries.ToList())
        {
            if (await ReloadAsync(entry) is null)
            {
                reloaded++;
            }
            else
            {
                failed++;
            }
        }

        return (reloaded, failed);
    }

    public async Task<NuGetClearResult> ClearAsync(NuGetPackageKey key, CancellationToken ct)
    {
        var result = await store.ClearPackageAsync(key, ct);
        await ChangedAsync("nuget.cleared", Describe(key), result);
        return result;
    }

    public async Task<NuGetClearResult> ClearFailedAsync(CancellationToken ct)
    {
        var result = await store.ClearByStateAsync(NuGetIndexState.Failed, ct);
        await ChangedAsync("nuget.cleared_failed", "failed packages", result);
        return result;
    }

    public async Task<NuGetClearResult> ClearAllAsync(CancellationToken ct)
    {
        var result = await store.ClearAllAsync(ct);
        await ChangedAsync("nuget.cleared_all", "all packages", result);
        return result;
    }

    public async Task<bool> RemoveCachedResultAsync(string cacheKey, CancellationToken ct)
    {
        var removed = await store.RemoveCachedResultAsync(cacheKey, ct);
        if (removed)
        {
            await AppendAsync("nuget.cached_result_removed", cacheKey, AuditOutcome.Ok, null, null);
            await events.PublishAsync(new NuGetCacheChanged());
        }

        return removed;
    }

    private async Task ChangedAsync(string action, string target, NuGetClearResult result)
    {
        await AppendAsync(action, target, AuditOutcome.Ok, $"{result.Packages} packages, {result.CachedResults} stored results", null);
        await events.PublishAsync(new NuGetCacheChanged());
    }

    private Task AppendAsync(string action, string target, AuditOutcome outcome, string? args, string? error) =>
        audit.AppendAsync(
            AuditEntries.Create(time, AuditCategory.Config, action, AuthDefaults.AdminActor, target, outcome, args, error),
            CancellationToken.None);

    private static string Describe(NuGetPackageKey key) => $"{key.PackageId} {key.Version} {key.TargetFramework}";

    private static string Short(string message) => message.Length <= MaxReasonLength ? message : message[..MaxReasonLength];
}
