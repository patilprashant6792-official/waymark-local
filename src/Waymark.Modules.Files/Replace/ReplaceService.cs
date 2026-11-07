using System.Globalization;
using Microsoft.Extensions.Logging;
using Waymark.Core.Security;

namespace Waymark.Modules.Files.Replace;

/// <summary>The facade of file_replace: checks the arguments, runs the planner for a preview, and the applier under the file write gate for an apply.</summary>
internal sealed class ReplaceService(
    IPathGuard guard,
    ReplacePlanner planner,
    ReplacePlanStore store,
    ReplaceApplier applier,
    FileWriteGate gate,
    IContentInspector inspector,
    ReplaceOptions options,
    ILogger<ReplaceService> logger) : IReplaceService
{
    public async Task<ReplacePreview> PreviewAsync(ReplaceRequest request, CancellationToken ct)
    {
        var validated = ReplaceRequestValidator.Validate(request, options);
        planner.ValidateNames(validated);
        var workspace = await guard.GetWorkspaceAsync(request.Workspace, ct).ConfigureAwait(false);
        await RejectSecretAsync(validated.With, ct).ConfigureAwait(false);

        var scan = await planner.ScanAsync(workspace, validated, ct).ConfigureAwait(false);
        var matchCount = scan.Files.Sum(file => file.Spans.Count);
        var blockers = Blockers(scan, matchCount);
        var plan = blockers.Count == 0
            ? store.Add(workspace.Id, validated.Signature, [.. scan.Files.Select(file => new FilePlan(file.RelativePath, file.Sha256, file.Spans))])
            : null;

        logger.LogInformation(
            "file_replace preview {Workspace}: {Matches} matches in {Files} files, plan {PlanId}",
            workspace.Name,
            matchCount,
            scan.Files.Count,
            plan?.Id ?? "none");

        var files = scan.Files.Select(file => new PreviewFile(file.RelativePath, file.Spans.Count, file.SyntaxErrors, file.Lines, file.MoreLines)).ToList();
        return new ReplacePreview(plan?.Id, plan?.ExpiresUtc, matchCount, files, scan.Summary, scan.Refused, blockers);
    }

    public async Task<ReplaceApplyResult> ApplyAsync(ReplaceRequest request, string planId, int confirmCount, IReadOnlyList<string> exclusions, CancellationToken ct)
    {
        var validated = ReplaceRequestValidator.Validate(request, options);
        planner.ValidateNames(validated);
        var parsedExclusions = ReplaceExclusions.Parse(exclusions);
        if (string.IsNullOrWhiteSpace(planId))
        {
            throw new ArgumentException("planId is empty. Run the preview (no planId) first.");
        }

        var workspace = await guard.GetWorkspaceAsync(request.Workspace, ct).ConfigureAwait(false);
        var plan = store.Find(planId.Trim());
        if (plan is null)
        {
            return ReplaceApplyResult.Refused(string.Create(
                CultureInfo.InvariantCulture,
                $"Plan '{planId.Trim()}' is unknown, expired or already used (a plan lasts {options.PlanMinutes} minutes and is used once). Run the preview again."));
        }

        if (plan.WorkspaceId != workspace.Id || !string.Equals(plan.Signature, validated.Signature, StringComparison.Ordinal))
        {
            return ReplaceApplyResult.Refused(
                "The arguments differ from the ones the plan was made with. Send the same workspace, find, with, mode, caseSensitive, wholeWord and pathGlob or pathsJson as the preview.");
        }

        var (kept, excluded) = ReplaceExclusions.Apply(plan.Files, parsedExclusions);
        var count = kept.Sum(file => file.Spans.Count);
        if (count == 0)
        {
            return ReplaceApplyResult.Refused("Every match of the plan is excluded; there is nothing to apply.");
        }

        if (count != confirmCount)
        {
            return ReplaceApplyResult.Refused(string.Create(
                CultureInfo.InvariantCulture,
                $"confirmCount is {confirmCount}, but the plan applies {count} replacement(s) after {excluded} exclusion(s). Check the preview and send that count."));
        }

        if (!store.Remove(plan.Id))
        {
            return ReplaceApplyResult.Refused("Another call used this plan first. Run the preview again.");
        }

        IReadOnlyList<AppliedFile> written;
        try
        {
            using (await gate.EnterAsync(ct).ConfigureAwait(false))
            {
                written = await applier.ApplyAsync(workspace, kept, validated.With, ct).ConfigureAwait(false);
            }
        }
        catch (ReplaceRefusedException ex)
        {
            logger.LogInformation("file_replace apply refused {Workspace} plan {PlanId}: {Reason}", workspace.Name, plan.Id, ex.Message);
            return ReplaceApplyResult.Refused(ex.Message);
        }

        logger.LogInformation(
            "file_replace applied {Workspace} plan {PlanId}: {Replacements} replacements in {Files} files, {Excluded} excluded",
            workspace.Name,
            plan.Id,
            count,
            written.Count,
            excluded);
        return new ReplaceApplyResult(true, null, written, excluded);
    }

    private List<string> Blockers(ScanResult scan, int matchCount)
    {
        var blockers = new List<string>();
        if (scan.Refused.Count > 0)
        {
            blockers.Add("Some files in pathsJson cannot be used (listed below). Fix pathsJson and preview again.");
        }

        if (scan.Summary.Truncated)
        {
            blockers.Add("The workspace has more files than one listing covers, so the scan is partial. Narrow pathGlob.");
        }

        if (scan.Summary.BudgetExceeded)
        {
            blockers.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The scan stopped after {options.ScanBudgetSeconds} seconds with {scan.Summary.Unscanned} file(s) unread, so it is partial. Narrow pathGlob."));
        }

        if (scan.Files.Count > options.MaxFiles)
        {
            blockers.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{scan.Files.Count} files match; one apply changes at most {options.MaxFiles}. Narrow pathGlob and replace in parts."));
        }

        if (matchCount > options.MaxMatches)
        {
            blockers.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{matchCount} matches; one apply changes at most {options.MaxMatches}. Narrow pathGlob or make find more specific."));
        }

        if (blockers.Count == 0 && matchCount == 0)
        {
            blockers.Add("Nothing matched, so there is nothing to apply.");
        }

        return blockers;
    }

    private async Task RejectSecretAsync(string with, CancellationToken ct)
    {
        if (with.Length == 0)
        {
            return;
        }

        var findings = await inspector.InspectAsync(with, ct).ConfigureAwait(false);
        if (findings.Count > 0)
        {
            throw new ArgumentException($"with contains {SensitiveDataScanner.Summarize(findings)}. Waymark does not write secrets into files.");
        }
    }
}
