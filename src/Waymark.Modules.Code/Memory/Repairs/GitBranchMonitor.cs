using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Waymark.Core.Events;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Memory.Repairs;

/// <summary>
/// Knows what is checked out in each workspace. <see cref="Current"/> reads .git/HEAD on every call (two small files, no git process), so a flow is always
/// judged against the branch that is checked out right now. Every few seconds the monitor reads every enabled workspace and publishes
/// <see cref="BranchChanged"/> when its head changed, so the drift scanner re-checks the workspace on a checkout made anywhere (terminal, IDE, another agent).
/// </summary>
internal sealed class GitBranchMonitor(IWorkspaceStore workspaces, IEventBus events, TimeProvider time, ILogger<GitBranchMonitor> logger) : BackgroundService, IWorkspaceBranches
{
    internal static readonly TimeSpan Every = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<long, WorkspaceHead> _known = new();

    public WorkspaceHead Current(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return GitHubRepoDetector.DetectHead(workspace.Path);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Every, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad poll must not stop branch watching: logged, and the next poll tries again.
                logger.LogWarning(ex, "Reading the checked-out branches failed; trying again in {Seconds} s.", Every.TotalSeconds);
                await Task.Delay(Every, time, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task PollAsync(CancellationToken ct)
    {
        var all = await workspaces.ListAsync(ct).ConfigureAwait(false);
        var live = new HashSet<long>();
        foreach (var workspace in all)
        {
            if (!workspace.Enabled)
            {
                continue;
            }

            live.Add(workspace.Id);
            var now = GitHubRepoDetector.DetectHead(workspace.Path);

            // The first read only learns the head: a start-up is not a checkout. The drift scanner's start-up sweep covers what changed while Waymark was off.
            if (_known.TryGetValue(workspace.Id, out var before) && before != now)
            {
                logger.LogInformation(
                    "Workspace {Workspace}: checked out {To} (was {From}); memory is checked against it.",
                    workspace.Name,
                    now.IsGit ? now.Branch ?? "a detached HEAD" : "no git checkout",
                    before.IsGit ? before.Branch ?? "a detached HEAD" : "no git checkout");
                _known[workspace.Id] = now;
                await events.PublishAsync(new BranchChanged(workspace.Id, before.Branch, now.Branch)).ConfigureAwait(false);
            }
            else
            {
                _known[workspace.Id] = now;
            }
        }

        foreach (var id in _known.Keys)
        {
            if (!live.Contains(id))
            {
                _known.TryRemove(id, out _);
            }
        }
    }
}
