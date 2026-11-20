using System.Diagnostics;
using System.Globalization;
using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Host.Memory;

/// <summary>The answer of the Test button: whether the provider embedded the test phrase, the size of the vector and how long it took. Never the key.</summary>
public sealed record MemoryTestResult(bool Ok, int Dimension, TimeSpan Latency, string Message);

/// <summary>
/// What the owner does with search by meaning on the Setup page and a workspace's Notes page: pick the provider and model, save, test and clear keys, switch it on
/// (only after a test of the current provider and model passed), re-embed everything, delete the stored vectors, and exclude a workspace.
/// Every change is audited under config.memory_*; the key never reaches the audit row, the log or the page.
/// </summary>
public sealed class MemoryAdminService(
    IMemorySettings settings,
    IEmbeddingProviders providers,
    IMemoryEmbeddingStore vectors,
    IMemoryIndexStatus status,
    IAuditStore audit,
    IEventBus events,
    TimeProvider time)
{
    private const string TestPhrase = "Waymark search by meaning: a short phrase to check the key and the model.";

    private string? _testedModelId;

    public IReadOnlyList<IEmbeddingProvider> Providers => providers.All;

    public MemoryIndexSnapshot Status => status.Current;

    public Task<MemorySettings> GetAsync(CancellationToken ct) => settings.GetAsync(ct);

    public Task<MemoryKeyInfo> GetKeyInfoAsync(string provider, CancellationToken ct) => settings.GetKeyInfoAsync(provider, ct);

    /// <summary>True when a test of exactly this provider, model and dimension passed since Waymark started.</summary>
    public bool IsTested(MemorySettings current) => current is not null && string.Equals(_testedModelId, current.ModelId, StringComparison.Ordinal);

    public async Task SetModelAsync(string provider, string model, int dimension, CancellationToken ct)
    {
        await settings.SetModelAsync(provider, model, dimension, AuthDefaults.AdminActor, ct).ConfigureAwait(false);
        await AuditAsync("config.memory_model_changed", string.Create(CultureInfo.InvariantCulture, $"{provider}/{model}@{dimension}"), ct).ConfigureAwait(false);
    }

    public async Task SaveKeyAsync(string provider, string key, CancellationToken ct)
    {
        await settings.SaveKeyAsync(provider, key, AuthDefaults.AdminActor, ct).ConfigureAwait(false);
        _testedModelId = null;
        await AuditAsync("config.memory_key_saved", provider, ct).ConfigureAwait(false);
    }

    public async Task ClearKeyAsync(string provider, CancellationToken ct)
    {
        await settings.ClearKeyAsync(provider, AuthDefaults.AdminActor, ct).ConfigureAwait(false);
        _testedModelId = null;
        await AuditAsync("config.memory_key_cleared", provider, ct).ConfigureAwait(false);
    }

    /// <summary>Embeds one fixed phrase with the active provider, model and dimension. A pass allows the switch to be turned on.</summary>
    public async Task<MemoryTestResult> TestAsync(CancellationToken ct)
    {
        var current = await settings.GetAsync(ct).ConfigureAwait(false);
        var provider = providers.Find(current.Provider);
        var key = provider is null ? null : await settings.GetKeyAsync(current.Provider, ct).ConfigureAwait(false);
        if (provider is null || key is null)
        {
            return new MemoryTestResult(false, 0, TimeSpan.Zero, "Save a key for this provider first.");
        }

        var watch = Stopwatch.StartNew();
        MemoryTestResult result;
        try
        {
            var batch = await provider.EmbedAsync([TestPhrase], EmbeddingPurpose.Document, current.Dimension, current.Model, key, ct).ConfigureAwait(false);
            result = new MemoryTestResult(true, batch.Dimension, watch.Elapsed, $"{provider.DisplayName} answered: {batch.Dimension} numbers per vector with {current.Model}.");
            _testedModelId = current.ModelId;
        }
        catch (EmbeddingException ex)
        {
            result = new MemoryTestResult(false, 0, watch.Elapsed, ex.Message);
        }

        await AuditAsync("config.memory_tested", $"{current.ModelId}: {(result.Ok ? "ok" : "failed")}", ct).ConfigureAwait(false);
        return result;
    }

    /// <exception cref="InvalidOperationException">Switching on before a test of the current provider and model passed.</exception>
    public async Task SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        if (enabled)
        {
            var current = await settings.GetAsync(ct).ConfigureAwait(false);
            if (!current.HasKey || !IsTested(current))
            {
                throw new InvalidOperationException("Test the key with the chosen provider and model first; the switch turns on after a test passes.");
            }
        }

        await settings.SetEnabledAsync(enabled, AuthDefaults.AdminActor, ct).ConfigureAwait(false);
        await AuditAsync(enabled ? "config.memory_enabled" : "config.memory_disabled", null, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes every stored vector, so the index embeds everything again (when on). The number deleted.</summary>
    public async Task<int> ReembedAllAsync(CancellationToken ct)
    {
        var removed = await vectors.DeleteAllAsync(ct).ConfigureAwait(false);
        await events.PublishAsync(new MemorySettingsChanged()).ConfigureAwait(false);
        await AuditAsync("config.memory_reembed", string.Create(CultureInfo.InvariantCulture, $"{removed} vectors dropped"), ct).ConfigureAwait(false);
        return removed;
    }

    /// <summary>Deletes every stored vector. While the switch is on they are made again; switch it off first to keep none.</summary>
    public async Task<int> DeleteVectorsAsync(CancellationToken ct)
    {
        var removed = await vectors.DeleteAllAsync(ct).ConfigureAwait(false);
        await events.PublishAsync(new MemorySettingsChanged()).ConfigureAwait(false);
        await AuditAsync("config.memory_vectors_deleted", string.Create(CultureInfo.InvariantCulture, $"{removed} vectors"), ct).ConfigureAwait(false);
        return removed;
    }

    public Task<bool> IsExcludedAsync(long workspaceId, CancellationToken ct) => settings.IsExcludedAsync(workspaceId, ct);

    public async Task SetExcludedAsync(Workspace workspace, bool excluded, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        await settings.SetExcludedAsync(workspace.Id, excluded, AuthDefaults.AdminActor, ct).ConfigureAwait(false);
        if (excluded)
        {
            // Deleted now, not on the next pass: the owner may switch search by meaning off right after.
            await vectors.DeleteWorkspaceAsync(workspace.Id, ct).ConfigureAwait(false);
        }

        await AuditAsync(excluded ? "config.memory_workspace_excluded" : "config.memory_workspace_included", workspace.Name, ct).ConfigureAwait(false);
    }

    private Task AuditAsync(string action, string? target, CancellationToken ct) =>
        audit.AppendAsync(AuditEntries.Create(time, AuditCategory.Config, action, AuthDefaults.AdminActor, target), ct);
}
