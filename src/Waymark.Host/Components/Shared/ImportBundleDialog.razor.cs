using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;
using Waymark.Core.Transfer;
using Waymark.Core.Workspaces;
using Waymark.Host.Transfer;

namespace Waymark.Host.Components.Shared;

/// <summary>
/// Reads a bundle file the owner chose and shows, workspace by workspace, what importing it adds: nothing is saved until Import is pressed, and what is here is never changed or removed.
/// The owner picks for each workspace of the file: merge into one that exists, create it in a folder of this computer, or skip it.
/// </summary>
public partial class ImportBundleDialog : LiveComponentBase
{
    private IReadOnlyList<Workspace> _existing = [];
    private IReadOnlyList<WorkspaceImported>? _report;
    private List<Row> _rows = [];
    private ParsedBundle? _parsed;
    private string? _error;
    private bool _busy;

    /// <summary>The workspace whose page opened the dialog; null on the Workspaces page. When set, only this workspace can receive.</summary>
    [Parameter]
    public long? TargetWorkspaceId { get; set; }

    /// <summary>The kinds of content this import applies; the rest of the file is ignored.</summary>
    [Parameter]
    public BundleContent Content { get; set; } = BundleContent.All;

    [CascadingParameter]
    private IMudDialogInstance Dialog { get; set; } = default!;

    [Inject]
    private WorkspaceBundleService Bundles { get; set; } = default!;

    [Inject]
    private IWorkspaceService Workspaces { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private ILogger<ImportBundleDialog> Logger { get; set; } = default!;

    private string Intro =>
        "Choose a bundle file that another Waymark exported. Before anything is saved you see what it adds"
        + (TargetWorkspaceId is null ? string.Empty : $" to this workspace's {ContentName}")
        + ". Import only adds: what you have here is never changed or removed.";

    private string ContentName => Content switch
    {
        BundleContent.AccessRules => "access rules",
        BundleContent.FeatureMaps => "feature maps",
        _ => "access rules and feature maps",
    };

    private bool CanImport =>
        !_busy && _report is null && _rows.Any(row =>
            row.Action != ImportAction.Skip
            && row.Preview is { Problem: null, Plan: { } plan } preview
            && (preview.CreatesWorkspace || plan.HasChanges));

    private async Task OnFileAsync(InputFileChangeEventArgs e)
    {
        _error = null;
        _parsed = null;
        _rows = [];
        _busy = true;
        try
        {
            string text;
            await using (var stream = e.File.OpenReadStream(WorkspaceBundleJson.MaxChars, Cancel))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                text = await reader.ReadToEndAsync(Cancel);
            }

            var parsed = await Bundles.ParseAsync(text, Cancel);
            var choices = await Bundles.SuggestAsync(parsed, TargetWorkspaceId, Cancel);
            _existing = await Workspaces.ListAsync(Cancel);
            _rows = [.. parsed.Bundle.Workspaces.Select(source => NewRow(source, choices))];
            _parsed = parsed;
            await RefreshPreviewAsync();
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed.
        }
        catch (ArgumentException ex)
        {
            _error = ex.Message;
        }
        catch (IOException ex)
        {
            Logger.LogDebug(ex, "The bundle file could not be read");
            _error = "The file is too large to be a Waymark bundle, or it could not be read.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The bundle file could not be read");
            _error = "The file could not be read.";
        }
        finally
        {
            _busy = false;
        }
    }

    private static Row NewRow(BundleWorkspace source, IReadOnlyList<ImportChoice> choices)
    {
        var choice = choices.FirstOrDefault(item => string.Equals(item.BundleName, source.Name, StringComparison.OrdinalIgnoreCase));
        return new Row(source)
        {
            Action = choice?.Action ?? ImportAction.Skip,
            TargetId = choice?.TargetWorkspaceId ?? 0,
            NewName = choice?.NewName ?? source.Name,
            NewPath = choice?.NewPath ?? string.Empty,
        };
    }

    private async Task ActionChangedAsync(Row row, ImportAction action)
    {
        row.Action = action;
        if (action == ImportAction.MergeIntoExisting)
        {
            if (TargetWorkspaceId is { } target)
            {
                // Only one workspace of the file can go into this one.
                row.TargetId = target;
                foreach (var other in _rows.Where(item => item != row && item.Action == ImportAction.MergeIntoExisting))
                {
                    other.Action = ImportAction.Skip;
                }
            }
            else if (row.TargetId == 0)
            {
                row.TargetId = (_existing.FirstOrDefault(workspace => string.Equals(workspace.Name, row.Source.Name, StringComparison.OrdinalIgnoreCase)) ?? _existing.FirstOrDefault())?.Id ?? 0;
            }
        }
        else if (action == ImportAction.CreateWorkspace && string.IsNullOrWhiteSpace(row.NewPath))
        {
            row.NewPath = row.Source.PathHint ?? string.Empty;
        }

        await RefreshPreviewAsync();
    }

    private async Task TargetChangedAsync(Row row, long id)
    {
        row.TargetId = id;
        await RefreshPreviewAsync();
    }

    private async Task NameChangedAsync(Row row, string name)
    {
        row.NewName = name ?? string.Empty;
        await RefreshPreviewAsync();
    }

    private async Task PathChangedAsync(Row row, string path)
    {
        row.NewPath = path ?? string.Empty;
        await RefreshPreviewAsync();
    }

    private List<ImportChoice> Choices() =>
        [.. _rows.Select(row => new ImportChoice(row.Source.Name, row.Action, row.TargetId == 0 ? null : row.TargetId, row.NewName, row.NewPath))];

    private async Task RefreshPreviewAsync()
    {
        if (_parsed is null)
        {
            return;
        }

        try
        {
            var previews = await Bundles.PreviewAsync(_parsed.Bundle, Choices(), Content, Cancel);
            foreach (var row in _rows)
            {
                row.Preview = previews.FirstOrDefault(preview => string.Equals(preview.BundleName, row.Source.Name, StringComparison.OrdinalIgnoreCase));
            }

            _error = null;
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The import preview could not be made");
            _error = "The preview could not be made.";
        }
    }

    private async Task ImportAsync()
    {
        if (_parsed is null || !CanImport)
        {
            return;
        }

        _busy = true;
        _error = null;
        try
        {
            _report = await Bundles.ImportAsync(_parsed.Bundle, Choices(), Content, Cancel);
            var done = _report.Count(item => item.Applied is not null);
            Snackbar.Add(done == 0 ? "Nothing was imported." : $"Imported {done} {(done == 1 ? "workspace" : "workspaces")}.", done == 0 ? Severity.Warning : Severity.Success);
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The bundle could not be imported");
            _error = "The import failed. Nothing was changed, except workspaces that may have been created.";
        }
        finally
        {
            _busy = false;
        }
    }

    private string FileSummary(BundleWorkspace source)
    {
        var parts = new List<string>();
        if (Content.HasFlag(BundleContent.AccessRules))
        {
            parts.Add(Count(source.AccessRules.Count, "access rule"));
        }

        if (Content.HasFlag(BundleContent.FeatureMaps))
        {
            parts.Add(Count(source.FeatureMaps.Count, "feature map"));
        }

        return "In the file: " + string.Join(", ", parts);
    }

    private IEnumerable<string> PlanLines(WorkspacePreview preview, WorkspaceMergePlan plan)
    {
        if (preview.CreatesWorkspace)
        {
            yield return $"A new workspace '{preview.TargetName}' is created.";
        }

        if (Content.HasFlag(BundleContent.AccessRules))
        {
            var line = $"Access rules: {plan.RulesToAdd.Count} to add";
            line += plan.RulesAlreadyThere > 0 ? $", {plan.RulesAlreadyThere} here already" : string.Empty;
            line += plan.RulesDifferent > 0 ? $" ({plan.RulesDifferent} switched on or off differently; yours stay as they are)" : string.Empty;
            line += plan.RulesOverLimit > 0 ? $", {plan.RulesOverLimit} not added (a workspace holds at most {Waymark.Core.Security.AccessRuleRules.MaxRulesPerWorkspace} rules)" : string.Empty;
            yield return line + ".";
        }

        if (Content.HasFlag(BundleContent.FeatureMaps))
        {
            var line = $"Feature maps: {plan.MapsNew} new, {plan.MapsExtended} gain components ({plan.ComponentsToAdd} components in all)";
            line += plan.MapsUnchanged > 0 ? $", {plan.MapsUnchanged} here already" : string.Empty;
            line += plan.ComponentsOverLimit > 0 ? $", {plan.ComponentsOverLimit} components not added (a map holds at most {Waymark.Core.Memory.FeatureMapRules.MaxComponentsPerMap})" : string.Empty;
            yield return line + ".";
        }

        if (!preview.CreatesWorkspace && !plan.HasChanges)
        {
            yield return "Nothing to add: everything in the file is here already.";
        }
    }

    private static string AllowText(WorkspaceMergePlan plan)
    {
        var types = string.Join(", ", plan.RulesToAdd.Where(rule => rule is { Kind: Waymark.Core.Security.AccessRuleKind.AllowExtension, Enabled: true }).Select(rule => rule.Pattern));
        return $"{Count(plan.RulesAllowingFileTypes, "rule lets", "rules let")} Claude open more file types here ({types}). Built-in blocks such as keys and databases still win. Import only if you made these rules.";
    }

    private static string ReportLine(WorkspaceImported item)
    {
        var applied = item.Applied;
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{(item.CreatedWorkspace ? "Workspace created. " : string.Empty)}Added {applied?.RulesAdded ?? 0} access rules, {applied?.MapsAdded ?? 0} new feature maps, and {applied?.ComponentsAdded ?? 0} components ({applied?.MapsExtended ?? 0} existing maps grew).");
        return text;
    }

    private static string Count(int number, string one, string? many = null) =>
        string.Create(CultureInfo.InvariantCulture, $"{number} {(number == 1 ? one : many ?? one + "s")}");

    private void Close() => Dialog.Close(DialogResult.Ok(true));

    private sealed class Row(BundleWorkspace source)
    {
        public BundleWorkspace Source { get; } = source;

        public ImportAction Action { get; set; }

        public long TargetId { get; set; }

        public string NewName { get; set; } = string.Empty;

        public string NewPath { get; set; } = string.Empty;

        public WorkspacePreview? Preview { get; set; }
    }
}
