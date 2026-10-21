using System.Text;
using ModelContextProtocol.Protocol;
using Waymark.Core.Audit;
using Waymark.Core.Security;
using Waymark.Core.Tools;
using Waymark.Host.Vault;

namespace Waymark.Host.Mcp;

/// <summary>
/// The one place every tool result passes before it goes to Claude. It (1) replaces vault secrets and secrets with a recognisable shape by
/// markers, (2) adds a note when something was hidden (and the optional closing line, off by default), and (3) writes what was hidden to the exposure
/// ledger. Tools do not need to do anything: the pipeline applies it to all of them, including their error messages.
/// </summary>
public sealed class ToolResultGuard(
    VaultService vault,
    IAuditStore audit,
    TimeProvider time,
    SafetyOptions options,
    ILogger<ToolResultGuard> logger)
{
    private const string VaultKind = "vault-secret";

    /// <summary>Hides secrets in every text block of the result, in place. Returns what was found (kinds and counts only).</summary>
    public async Task<IReadOnlyList<RedactionFinding>> RedactAsync(CallToolResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        var findings = new List<RedactionFinding>();
        if (!options.RedactSecrets)
        {
            return findings;
        }

        foreach (var block in result.Content.OfType<TextContentBlock>())
        {
            var text = block.Text;
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var cleaned = await ScrubKnownSecretsAsync(text, ct);
            if (!string.Equals(cleaned, text, StringComparison.Ordinal))
            {
                SensitiveDataScanner.Add(findings, VaultKind, 1);
            }

            var scanned = SensitiveDataScanner.Redact(cleaned);
            foreach (var finding in scanned.Findings)
            {
                SensitiveDataScanner.Add(findings, finding.Kind, finding.Count);
            }

            block.Text = scanned.Text;
        }

        return findings;
    }

    /// <summary>Adds the closing note as a last text block and returns its size in bytes (0 when there is nothing to add).</summary>
    public int AppendNotice(CallToolResult result, IReadOnlyList<RedactionFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(findings);
        var parts = new List<string>(3);
        var hidden = findings.Where(f => f.Kind != SensitiveDataScanner.IncompleteKind).ToList();
        if (hidden.Count > 0)
        {
            parts.Add(
                $"Waymark hid {SensitiveDataScanner.Summarize(hidden)} in this output ({SensitiveDataScanner.MarkerPrefix}...] markers). " +
                "Never write a marker into a file or command; tell the user to rotate the affected credentials.");
        }

        if (findings.Any(f => f.Kind == SensitiveDataScanner.IncompleteKind))
        {
            parts.Add("Waymark could not finish checking this output for secrets.");
        }

        if (options.AppendFooter)
        {
            parts.Add(options.FooterText);
        }

        if (parts.Count == 0)
        {
            return 0;
        }

        var text = string.Join(' ', parts);
        result.Content.Add(new TextContentBlock { Text = text });
        return Encoding.UTF8.GetByteCount(text);
    }

    /// <summary>Writes one ledger row (kinds and counts, never a value) and one line to the live log.</summary>
    public async Task RecordAsync(IReadOnlyList<RedactionFinding> findings, string tool, string client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(findings);
        if (findings.Count == 0)
        {
            return;
        }

        var summary = SensitiveDataScanner.Summarize(findings);
        logger.LogWarning("Waymark hid {Summary} in the output of {Tool}", summary, tool);
        try
        {
            await audit.AppendAsync(AuditEntries.Create(time, AuditCategory.Config, ExposureLedger.Action, client, tool, args: summary), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not write the exposure ledger row for tool {Tool}", tool);
        }
    }

    // A vault that cannot be read must not stop the tools: the pattern check still runs on the text.
    private async Task<string> ScrubKnownSecretsAsync(string text, CancellationToken ct)
    {
        try
        {
            return await vault.ScrubAsync(text, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The vault could not be used to check a tool result; the pattern check still ran");
            return text;
        }
    }
}
