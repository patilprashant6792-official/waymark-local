using Waymark.Core.Security;

namespace Waymark.Host.Vault;

/// <summary>The copy tools' secret check: the vault values first, then the credential shapes the result guard also looks for.</summary>
public sealed class ContentInspector(VaultService vault, ILogger<ContentInspector> logger) : IContentInspector
{
    private const string VaultKind = "vault-secret";
    private const string MarkerKind = "redaction-marker";

    public async Task<IReadOnlyList<RedactionFinding>> InspectAsync(string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        var findings = new List<RedactionFinding>();
        if (text.Length == 0)
        {
            return findings;
        }

        if (await ContainsVaultValueAsync(text, ct).ConfigureAwait(false))
        {
            SensitiveDataScanner.Add(findings, VaultKind, 1);
        }

        foreach (var finding in SensitiveDataScanner.Redact(text).Findings)
        {
            SensitiveDataScanner.Add(findings, finding.Kind, finding.Count);
        }

        if (SensitiveDataScanner.ContainsMarker(text))
        {
            SensitiveDataScanner.Add(findings, MarkerKind, 1);
        }

        return findings;
    }

    // A vault that cannot be read must not stop the copy: the pattern check still runs.
    private async Task<bool> ContainsVaultValueAsync(string text, CancellationToken ct)
    {
        try
        {
            var scrubbed = await vault.ScrubAsync(text, ct).ConfigureAwait(false);
            return !string.Equals(scrubbed, text, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The vault could not be used to check copied text; the pattern check still ran");
            return false;
        }
    }
}
