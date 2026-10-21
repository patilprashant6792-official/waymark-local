using Microsoft.Extensions.Logging;
using Waymark.Core.Security;

namespace Waymark.Core.Guides;

/// <summary>
/// The owner's text for the overview and the rendering of the whole. The text is read once and kept in memory; this class is its only writer,
/// so the copy stays current and the call pipeline can ask for the hash on every call without touching the database.
/// </summary>
public sealed class HostPrompt(IGuideStore store, ISetupSummary setup, TimeProvider time, ILogger<HostPrompt> logger) : IHostPrompt, IDisposable
{
    private static readonly HostPromptText Empty = new(string.Empty, null, null);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile HostPromptText? _owner;

    public async Task<HostPromptText> GetOwnerTextAsync(CancellationToken ct)
    {
        var cached = _owner;
        if (cached is not null)
        {
            return cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_owner is { } loadedMeanwhile)
            {
                return loadedMeanwhile;
            }

            var rows = await store.ListOverridesAsync(ct);
            var row = rows.FirstOrDefault(r => string.Equals(r.Module, HostPromptRules.StoreKey, StringComparison.OrdinalIgnoreCase));
            var loaded = row is null ? Empty : new HostPromptText(row.Content, row.ChangedUtc, row.ChangedBy);
            _owner = loaded;
            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveOwnerTextAsync(string text, string changedBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changedBy);
        var clean = GuideRules.Normalize(text ?? string.Empty);
        Validate(clean);

        await _gate.WaitAsync(ct);
        try
        {
            if (clean.Length == 0)
            {
                await store.DeleteOverrideAsync(HostPromptRules.StoreKey, ct);
                _owner = Empty;
                return;
            }

            var now = time.GetUtcNow();
            await store.SetOverrideAsync(HostPromptRules.StoreKey, clean, changedBy, now, ct);
            _owner = new HostPromptText(clean, now, changedBy);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> HashAsync(HostTemplate template, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(template);
        var owner = await GetOwnerTextAsync(ct);
        return GuideRules.Hash(template.Stable(HostPromptRules.OwnerBlock(owner.Content)));
    }

    public async Task<string> RenderAsync(HostTemplate template, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(template);
        var owner = await GetOwnerTextAsync(ct);
        return template.Render(HostPromptRules.OwnerBlock(owner.Content), await DescribeSetupAsync(ct));
    }

    public void Dispose() => _gate.Dispose();

    private static void Validate(string text)
    {
        if (text.Length > HostPromptRules.MaxOwnerChars)
        {
            throw new ArgumentException(
                $"The text is {text.Length:N0} characters; the limit is {HostPromptRules.MaxOwnerChars:N0}. Every agent that loads the overview receives all of it, so keep it focused.");
        }

        if (SensitiveDataScanner.Redact(text).HasFindings)
        {
            throw new ArgumentException("The text looks like it contains a secret. Every agent that loads the overview receives it, so remove the secret.");
        }
    }

    // A failure here costs the agent the live setup only; the rest of the overview is still worth sending.
    private async Task<string> DescribeSetupAsync(CancellationToken ct)
    {
        try
        {
            return await setup.DescribeAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The live setup could not be described for the overview");
            return "The live setup could not be read just now. Call waymark_guide again later for it.";
        }
    }
}
