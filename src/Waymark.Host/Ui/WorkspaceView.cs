using Waymark.Core.Code;
using Waymark.Core.Workspaces;
using Waymark.Host.Components.Shared;

namespace Waymark.Host.Ui;

/// <summary>What a workspace says about itself on its card and on its details page, worded the same way on both.</summary>
public static class WorkspaceView
{
    public static (StatusState State, string Text) StatusOf(Workspace workspace, CodeIndexProgress? progress)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!workspace.Enabled)
        {
            return (StatusState.Off, "Disabled");
        }

        if (progress is { Phase: CodeIndexPhase.Failed })
        {
            return (StatusState.Failed, "Index failed");
        }

        return IsIndexing(progress) ? (StatusState.Working, "Indexing") : (StatusState.Healthy, "Ready");
    }

    public static bool IsIndexing(CodeIndexProgress? progress) => progress is { Phase: CodeIndexPhase.Started or CodeIndexPhase.Running };

    public static string ProgressText(CodeIndexProgress? progress) =>
        progress is { Phase: CodeIndexPhase.Started or CodeIndexPhase.Running } running
            ? $"{DisplayFormat.Count(running.FilesProcessed)} of {DisplayFormat.Count(running.FilesTotal)} files"
            : string.Empty;

    public static string StatsText(CodeIndexStats? stats) =>
        stats is null
            ? "-"
            : $"{DisplayFormat.Count(stats.Files)} files, {DisplayFormat.Count(stats.Types)} types, {DisplayFormat.Count(stats.Members)} members";

    /// <summary>The languages of a workspace, the biggest first, as (name, files).</summary>
    public static IReadOnlyList<(string Name, int Files)> LanguagesOf(CodeIndexStats? stats) =>
        stats?.FilesByLanguage is { Count: > 0 } languages
            ? languages.Where(pair => pair.Value > 0).OrderByDescending(pair => pair.Value).Select(pair => (CodeLanguages.DisplayName(pair.Key), pair.Value)).ToList()
            : [];

    /// <summary>The colour class of a card for its state; none for a workspace that is off.</summary>
    public static string AccentOf(StatusState state) => state switch
    {
        StatusState.Healthy => "ok",
        StatusState.Attention => "warn",
        StatusState.Failed => "err",
        StatusState.Working => "info",
        _ => string.Empty
    };

    /// <summary>The GitHub repository of every workspace that is the root of a checkout, looked up off the UI thread.</summary>
    public static Task<Dictionary<long, GitHubRepo>> DetectReposAsync(IEnumerable<Workspace> workspaces, CancellationToken ct)
    {
        var folders = workspaces.Select(workspace => (workspace.Id, workspace.Path)).ToList();
        return Task.Run(
            () =>
            {
                var found = new Dictionary<long, GitHubRepo>();
                foreach (var (id, folder) in folders)
                {
                    if (GitHubRepoDetector.Detect(folder) is { } repo)
                    {
                        found[id] = repo;
                    }
                }

                return found;
            },
            ct);
    }
}
