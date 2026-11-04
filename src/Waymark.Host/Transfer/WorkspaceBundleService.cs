using System.Globalization;
using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Security;
using Waymark.Core.Transfer;
using Waymark.Core.Workspaces;
using Waymark.Modules.Code.Scheduling;

namespace Waymark.Host.Transfer;

/// <summary>What the owner decides for one workspace of a bundle.</summary>
public enum ImportAction
{
    Skip,
    MergeIntoExisting,
    CreateWorkspace,
}

/// <param name="BundleName">The workspace as the file names it.</param>
/// <param name="TargetWorkspaceId">With MergeIntoExisting: the workspace on this computer that receives the rules and maps.</param>
/// <param name="NewName">With CreateWorkspace: the name of the new workspace.</param>
/// <param name="NewPath">With CreateWorkspace: the folder of the new workspace on this computer.</param>
public sealed record ImportChoice(string BundleName, ImportAction Action, long? TargetWorkspaceId, string NewName, string NewPath);

/// <summary>A bundle after it was read and cleaned, and what was left out of the file.</summary>
public sealed record ParsedBundle(WorkspaceBundle Bundle, IReadOnlyList<string> Notes);

/// <summary>What importing one workspace would do. Plan is null when it cannot be done; Problem says why.</summary>
public sealed record WorkspacePreview(string BundleName, string? TargetName, bool CreatesWorkspace, WorkspaceMergePlan? Plan, string? Problem);

/// <summary>What importing one workspace did. Applied is null when nothing was written; Problem says why.</summary>
public sealed record WorkspaceImported(string BundleName, string? TargetName, bool CreatedWorkspace, WorkspaceApplied? Applied, string? Problem);

/// <summary>A bundle ready to be downloaded.</summary>
public sealed record BundleFile(string FileName, string Text);

/// <summary>
/// Moves the workspaces, their access rules and their feature maps to another computer: export writes one file, import reads it back after the owner has seen exactly what it adds.
/// Import only adds, never changes or removes what is here. It is reachable from the admin UI only; no MCP tool calls it, so Claude can neither export the rules nor widen them by importing.
/// </summary>
public sealed class WorkspaceBundleService(
    IWorkspaceService workspaces,
    IAccessRuleStore rules,
    IAccessRuleCache ruleCache,
    IFeatureMapStore maps,
    IWorkspaceBundleStore bundleStore,
    IContentInspector inspector,
    IIndexScheduler scheduler,
    IAuditStore audit,
    IEventBus events,
    TimeProvider time,
    ILogger<WorkspaceBundleService> logger)
{
    private const int MapPageSize = 200;
    private const int MapChunk = 100;

    /// <summary>One workspace (<paramref name="workspaceId"/>) or all of them, with the kinds of content asked for.</summary>
    /// <exception cref="KeyNotFoundException">The workspace no longer exists.</exception>
    /// <exception cref="InvalidOperationException">There is nothing to export, or the text looks like it holds a secret, so no file is made.</exception>
    public async Task<BundleFile> ExportAsync(long? workspaceId, BundleContent content, CancellationToken ct)
    {
        var all = await workspaces.ListAsync(ct).ConfigureAwait(false);
        var selected = workspaceId is null ? all : [.. all.Where(workspace => workspace.Id == workspaceId)];
        if (selected.Count == 0)
        {
            throw workspaceId is null
                ? new InvalidOperationException("There are no workspaces to export yet.")
                : new KeyNotFoundException("The workspace no longer exists.");
        }

        var items = new List<BundleWorkspace>();
        foreach (var workspace in selected)
        {
            var exportedRules = new List<BundleRule>();
            if (content.HasFlag(BundleContent.AccessRules))
            {
                var stored = await rules.ListAsync(workspace.Id, ct).ConfigureAwait(false);
                exportedRules.AddRange(stored.Select(rule => new BundleRule(rule.Kind, rule.Pattern, rule.Note, rule.Enabled, rule.CreatedUtc)));
            }

            var exportedMaps = new List<BundleMap>();
            if (content.HasFlag(BundleContent.FeatureMaps))
            {
                var stored = await LoadMapsAsync(workspace.Id, ct).ConfigureAwait(false);
                exportedMaps.AddRange(stored
                    .Where(map => map.Steps.Count > 0)
                    .Select(map => new BundleMap(
                        map.Name,
                        map.Summary,
                        map.UpdatedUtc,
                        [.. map.Steps.Select(step => new BundleComponent(step.Spec, step.Note, step.Depth, step.Link))],
                        map.Ordered)));
            }

            items.Add(new BundleWorkspace(workspace.Name, workspace.Path, workspace.Enabled, exportedRules, exportedMaps));
        }

        var now = time.GetUtcNow();
        var text = WorkspaceBundleJson.Serialize(new WorkspaceBundle(WorkspaceBundleJson.FormatName, WorkspaceBundleJson.CurrentVersion, now, items));
        if ((await inspector.InspectAsync(text, ct).ConfigureAwait(false)).Count > 0)
        {
            throw new InvalidOperationException("Some text looks like it holds a secret or a redaction marker, so no file was made. Find it in the rules or feature maps, remove it, and export again.");
        }

        var label = workspaceId is null ? "workspaces" : selected[0].Name;
        var kind = content switch
        {
            BundleContent.AccessRules => "-access-rules",
            BundleContent.FeatureMaps => "-feature-maps",
            _ => string.Empty,
        };
        var fileName = string.Create(CultureInfo.InvariantCulture, $"waymark-{label}{kind}-{now:yyyyMMdd-HHmmss}.json");
        await AuditAsync("config.bundle_exported", workspaceId is null ? "all workspaces" : selected[0].Name, Describe(content, items), ct).ConfigureAwait(false);
        return new BundleFile(fileName, text);
    }

    /// <summary>Reads the text of a file the owner chose and cleans it.</summary>
    /// <exception cref="ArgumentException">The text is not a bundle this Waymark can import; the message says why.</exception>
    public async Task<ParsedBundle> ParseAsync(string text, CancellationToken ct)
    {
        if (!WorkspaceBundleJson.TryParse(text, out var bundle, out var error))
        {
            throw new ArgumentException(error);
        }

        if ((await inspector.InspectAsync(text, ct).ConfigureAwait(false)).Count > 0)
        {
            throw new ArgumentException("The file looks like it holds a secret or a redaction marker, so it was not read. Open it, take that out, and try again.");
        }

        var clean = WorkspaceBundleSanitizer.Sanitize(bundle);
        if (clean.Bundle.Workspaces.Count == 0)
        {
            throw new ArgumentException("The file has no workspace that Waymark can import." + (clean.Notes.Count > 0 ? " " + clean.Notes[0] : string.Empty));
        }

        return new ParsedBundle(clean.Bundle, clean.Notes);
    }

    /// <summary>
    /// A first choice for every workspace of the file: merge into the one of the same name, otherwise create it in the folder it had. Inside one workspace's page
    /// (<paramref name="targetWorkspaceId"/>) only that workspace can receive, the one of the same name or the only one in the file.
    /// </summary>
    public async Task<IReadOnlyList<ImportChoice>> SuggestAsync(ParsedBundle parsed, long? targetWorkspaceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        var existing = await workspaces.ListAsync(ct).ConfigureAwait(false);
        var target = targetWorkspaceId is null ? null : existing.FirstOrDefault(workspace => workspace.Id == targetWorkspaceId);
        var choices = new List<ImportChoice>();
        foreach (var source in parsed.Bundle.Workspaces)
        {
            var hint = source.PathHint ?? string.Empty;
            if (targetWorkspaceId is not null)
            {
                var fits = target is not null && (parsed.Bundle.Workspaces.Count == 1 || string.Equals(target.Name, source.Name, StringComparison.OrdinalIgnoreCase));
                choices.Add(new ImportChoice(source.Name, fits ? ImportAction.MergeIntoExisting : ImportAction.Skip, fits ? targetWorkspaceId : null, source.Name, hint));
                continue;
            }

            var same = existing.FirstOrDefault(workspace => string.Equals(workspace.Name, source.Name, StringComparison.OrdinalIgnoreCase));
            choices.Add(same is null
                ? new ImportChoice(source.Name, ImportAction.CreateWorkspace, null, source.Name, hint)
                : new ImportChoice(source.Name, ImportAction.MergeIntoExisting, same.Id, source.Name, hint));
        }

        return choices;
    }

    /// <summary>What the import would add, workspace by workspace, without changing anything.</summary>
    public async Task<IReadOnlyList<WorkspacePreview>> PreviewAsync(WorkspaceBundle bundle, IReadOnlyList<ImportChoice> choices, BundleContent content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(choices);
        var existing = await workspaces.ListAsync(ct).ConfigureAwait(false);
        var previews = new List<WorkspacePreview>();
        foreach (var step in await ResolveAsync(bundle, choices, existing, ct).ConfigureAwait(false))
        {
            if (step.Problem is not null || step.Source is null)
            {
                previews.Add(new WorkspacePreview(step.Choice.BundleName, step.Existing?.Name, step.Creates, null, step.Problem));
                continue;
            }

            var plan = await PlanAsync(step.Source, content, step.Existing, ct).ConfigureAwait(false);
            previews.Add(new WorkspacePreview(step.Choice.BundleName, step.Existing?.Name ?? step.Choice.NewName.Trim(), step.Creates, plan, null));
        }

        return previews;
    }

    /// <summary>
    /// Creates the workspaces that are new, then writes every rule and map in one transaction. The plans are made again here from what is stored now, so a change since the preview is
    /// respected. Run again with the same file, it adds nothing: what is there is never added twice.
    /// </summary>
    public async Task<IReadOnlyList<WorkspaceImported>> ImportAsync(WorkspaceBundle bundle, IReadOnlyList<ImportChoice> choices, BundleContent content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(choices);
        var existing = await workspaces.ListAsync(ct).ConfigureAwait(false);
        var results = new List<WorkspaceImported>();
        var applies = new List<WorkspaceApply>();
        var pending = new List<(ImportChoice Choice, Workspace Target, bool Created)>();
        foreach (var step in await ResolveAsync(bundle, choices, existing, ct).ConfigureAwait(false))
        {
            if (step.Problem is not null || step.Source is null)
            {
                results.Add(new WorkspaceImported(step.Choice.BundleName, step.Existing?.Name, false, null, step.Problem));
                continue;
            }

            var target = step.Existing;
            var created = false;
            if (target is null)
            {
                var added = await workspaces.AddAsync(step.Choice.NewName.Trim(), step.Choice.NewPath, ct).ConfigureAwait(false);
                if (!added.IsSuccess || added.Workspace is null)
                {
                    results.Add(new WorkspaceImported(step.Choice.BundleName, step.Choice.NewName.Trim(), false, null, added.Error ?? "The workspace could not be created."));
                    continue;
                }

                target = added.Workspace;
                created = true;
                if (!step.Source.Enabled)
                {
                    await workspaces.SetEnabledAsync(target.Id, false, ct).ConfigureAwait(false);
                }
                else
                {
                    scheduler.RequestReconcile(target.Id);
                }
            }

            applies.Add(new WorkspaceApply(target.Id, await PlanAsync(step.Source, content, created ? null : target, ct).ConfigureAwait(false)));
            pending.Add((step.Choice, target, created));
        }

        if (applies.Count == 0)
        {
            return results;
        }

        IReadOnlyList<WorkspaceApplied> applied;
        try
        {
            applied = await bundleStore.ApplyAsync(applies, time.GetUtcNow(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The rules and feature maps of a bundle could not be written");
            results.AddRange(pending.Select(item => new WorkspaceImported(item.Choice.BundleName, item.Target.Name, item.Created, null, "Nothing was written: the rules and feature maps could not be stored.")));
            return results;
        }

        foreach (var (item, written) in pending.Zip(applied))
        {
            results.Add(new WorkspaceImported(item.Choice.BundleName, item.Target.Name, item.Created, written, null));
            ruleCache.Invalidate(item.Target.Id);
            await AuditAsync("config.bundle_imported", item.Target.Name, DescribeApplied(written, item.Created), ct).ConfigureAwait(false);
            if (written.RulesAdded > 0)
            {
                await events.PublishAsync(new AccessRulesChanged(item.Target.Id)).ConfigureAwait(false);
            }

            if (written.MapsAdded > 0)
            {
                await events.PublishAsync(new FeatureMapsChanged(item.Target.Id)).ConfigureAwait(false);

                // More than one flow arrived: whatever indexes flows builds the workspace again.
                await events.PublishAsync(new FlowChanged(item.Target.Id, 0, FlowChange.Imported)).ConfigureAwait(false);
            }
        }

        return results;
    }

    private async Task<WorkspaceMergePlan> PlanAsync(BundleWorkspace source, BundleContent content, Workspace? existing, CancellationToken ct)
    {
        if (existing is null)
        {
            return BundleMergePlanner.Plan(source, content, [], []);
        }

        var storedRules = content.HasFlag(BundleContent.AccessRules) ? await rules.ListAsync(existing.Id, ct).ConfigureAwait(false) : [];
        var storedMaps = content.HasFlag(BundleContent.FeatureMaps) ? await LoadMapsAsync(existing.Id, ct).ConfigureAwait(false) : [];
        return BundleMergePlanner.Plan(source, content, storedRules, storedMaps);
    }

    private sealed record Step(ImportChoice Choice, BundleWorkspace? Source, Workspace? Existing, bool Creates, string? Problem);

    private async Task<List<Step>> ResolveAsync(WorkspaceBundle bundle, IReadOnlyList<ImportChoice> choices, IReadOnlyList<Workspace> existing, CancellationToken ct)
    {
        var steps = new List<Step>();
        var newNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in choices)
        {
            if (choice.Action == ImportAction.Skip)
            {
                continue;
            }

            var source = bundle.Workspaces.FirstOrDefault(workspace => string.Equals(workspace.Name, choice.BundleName, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                steps.Add(new Step(choice, null, null, false, "This workspace is not in the file."));
                continue;
            }

            if (choice.Action == ImportAction.MergeIntoExisting)
            {
                var target = existing.FirstOrDefault(workspace => workspace.Id == choice.TargetWorkspaceId);
                steps.Add(target is null
                    ? new Step(choice, source, null, false, "Choose the workspace that receives it.")
                    : new Step(choice, source, target, false, null));
                continue;
            }

            steps.Add(await ResolveNewAsync(choice, source, existing, newNames, ct).ConfigureAwait(false));
        }

        return steps;
    }

    private async Task<Step> ResolveNewAsync(ImportChoice choice, BundleWorkspace source, IReadOnlyList<Workspace> existing, HashSet<string> newNames, CancellationToken ct)
    {
        var name = choice.NewName?.Trim();
        var nameError = WorkspacePathRules.ValidateName(name);
        if (nameError is not null)
        {
            return new Step(choice, source, null, true, nameError);
        }

        if (existing.Any(workspace => string.Equals(workspace.Name, name, StringComparison.OrdinalIgnoreCase)) || !newNames.Add(name!))
        {
            return new Step(choice, source, null, true, $"A workspace named '{name}' exists already. Merge into it, or use another name.");
        }

        var folder = await workspaces.ValidateAsync(choice.NewPath, ct).ConfigureAwait(false);
        if (!folder.IsValid)
        {
            return new Step(choice, source, null, true, folder.Error ?? "The folder is not valid.");
        }

        var taken = existing.FirstOrDefault(workspace => string.Equals(workspace.Path, folder.NormalizedPath, StringComparison.OrdinalIgnoreCase));
        return taken is null
            ? new Step(choice, source, null, true, null)
            : new Step(choice, source, null, true, $"This folder is the workspace '{taken.Name}' already. Merge into it instead.");
    }

    private async Task<List<FeatureMap>> LoadMapsAsync(long workspaceId, CancellationToken ct)
    {
        var ids = new List<long>();
        var skip = 0;
        while (true)
        {
            var page = await maps.ListAsync(workspaceId, null, skip, MapPageSize, ct).ConfigureAwait(false);
            ids.AddRange(page.Items.Select(item => item.Id));
            skip += page.Items.Count;
            if (page.Items.Count == 0 || skip >= page.Total)
            {
                break;
            }
        }

        var all = new List<FeatureMap>();
        foreach (var chunk in ids.Chunk(MapChunk))
        {
            all.AddRange(await maps.GetAsync(workspaceId, chunk, ct).ConfigureAwait(false));
        }

        return all;
    }

    private Task AuditAsync(string action, string target, string args, CancellationToken ct) =>
        audit.AppendAsync(AuditEntries.Create(time, AuditCategory.Config, action, AuthDefaults.AdminActor, target, AuditOutcome.Ok, args), ct);

    private static string Describe(BundleContent content, IReadOnlyList<BundleWorkspace> items) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{content}: {items.Count} workspaces, {items.Sum(item => item.AccessRules.Count)} rules, {items.Sum(item => item.FeatureMaps.Count)} maps");

    private static string DescribeApplied(WorkspaceApplied applied, bool created) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{(created ? "workspace created; " : string.Empty)}rules +{applied.RulesAdded}; maps +{applied.MapsAdded} new, {applied.StepsAdded} steps");
}
