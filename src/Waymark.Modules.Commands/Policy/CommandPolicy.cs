using Waymark.Core.Commands;
using Waymark.Core.Workspaces;
using Waymark.Modules.Commands.Execution;

namespace Waymark.Modules.Commands.Policy;

/// <param name="Gate">What happens now: Off (refuse), Ask (wait for the owner) or Auto (run).</param>
/// <param name="AskReason">Why the owner is asked, in plain words. Null for Off and Auto.</param>
/// <param name="Fingerprint">The build files as they are now; null when the operation does not look at them.</param>
/// <param name="IsClean">True when the build files are exactly as the owner last approved them.</param>
public sealed record PolicyOutcome(CommandGate Gate, string? AskReason, IReadOnlyDictionary<string, string>? Fingerprint, bool IsClean);

/// <summary>Decides, for one request, whether it is refused, needs the owner or may run: the owner's gate choice plus the check of the build files against the approved baseline.</summary>
public sealed class CommandPolicy(ICommandPolicyStore store)
{
    private const int MaxListedFiles = 6;

    public async Task<PolicyOutcome> EvaluateAsync(OperationInfo operation, Workspace? workspace, bool readFingerprint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var settings = await store.ListGatesAsync(ct).ConfigureAwait(false);
        var gate = CommandCatalog.Resolve(operation, workspace?.Id, settings);
        if (gate == CommandGate.Off)
        {
            return new PolicyOutcome(CommandGate.Off, null, null, false);
        }

        IReadOnlyDictionary<string, string>? fingerprint = null;
        var isClean = false;
        var reasons = new List<string>();
        if (workspace is not null && (operation.DriftSensitive || readFingerprint))
        {
            fingerprint = await BuildFingerprint.ComputeAsync(workspace.Path, ct).ConfigureAwait(false);
            var trust = await store.GetTrustAsync(workspace.Id, ct).ConfigureAwait(false);
            if (trust is null)
            {
                if (operation.DriftSensitive)
                {
                    reasons.Add("Nothing has been approved in this workspace yet. Approve once and Waymark remembers the state of its project and git files.");
                    gate = Lower(gate);
                }
            }
            else
            {
                var changed = BuildFingerprint.Differences(BuildFingerprint.Deserialize(trust.FingerprintJson), fingerprint);
                isClean = changed.Count == 0;
                if (!isClean && operation.DriftSensitive)
                {
                    reasons.Add("Project or git files changed since you last approved a command: " + Describe(changed));
                    gate = Lower(gate);
                }
            }
        }

        if (gate == CommandGate.Ask && reasons.Count == 0)
        {
            reasons.Add(operation.Key == CommandCatalog.ShellRun
                ? "Shell commands always need your approval."
                : $"Your setting for '{operation.Label}' is Ask.");
        }

        return new PolicyOutcome(gate, gate == CommandGate.Ask ? string.Join("\n", reasons) : null, fingerprint, isClean);
    }

    /// <summary>Remembers the build files as approved. Called when the owner approves a command and after commands that edit those files themselves.</summary>
    public Task TrustAsync(long workspaceId, IReadOnlyDictionary<string, string> fingerprint, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        return store.SetTrustAsync(workspaceId, BuildFingerprint.Serialize(fingerprint), now, ct);
    }

    private static CommandGate Lower(CommandGate gate) => gate == CommandGate.Auto ? CommandGate.Ask : gate;

    private static string Describe(IReadOnlyList<string> changed)
    {
        var shown = string.Join(", ", changed.Take(MaxListedFiles));
        return changed.Count > MaxListedFiles ? $"{shown} and {changed.Count - MaxListedFiles} more" : shown;
    }
}
