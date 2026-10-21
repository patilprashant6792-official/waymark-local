using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Events;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Host.Security;

/// <summary>
/// Lets the owner add, switch off and remove the access rules of a workspace. Every change takes effect at once
/// (the rule cache is cleared first) and is written to the audit trail.
/// </summary>
public sealed class AccessRuleService(
    IAccessRuleStore store,
    IAccessRuleCache cache,
    IWorkspaceStore workspaces,
    IAuditStore audit,
    IEventBus events,
    TimeProvider time)
{
    public async Task<Workspace?> FindWorkspaceAsync(long workspaceId, CancellationToken ct) =>
        (await workspaces.ListAsync(ct)).FirstOrDefault(workspace => workspace.Id == workspaceId);

    public Task<IReadOnlyList<AccessRule>> ListAsync(long workspaceId, CancellationToken ct) => store.ListAsync(workspaceId, ct);

    /// <exception cref="ArgumentException">The pattern or note is not acceptable, the rule exists already, or the workspace has too many rules.</exception>
    /// <exception cref="KeyNotFoundException">The workspace no longer exists.</exception>
    public async Task<AccessRule> AddAsync(long workspaceId, AccessRuleKind kind, string? pattern, string? note, CancellationToken ct)
    {
        var workspace = await FindWorkspaceAsync(workspaceId, ct) ?? throw new KeyNotFoundException("The workspace no longer exists.");
        if (!AccessRuleRules.TryNormalize(kind, pattern, out var normalized, out var error))
        {
            throw new ArgumentException(error);
        }

        var cleanNote = note?.Trim() ?? string.Empty;
        if (!AccessRuleRules.IsValidNote(cleanNote))
        {
            throw new ArgumentException($"A note can have at most {AccessRuleRules.MaxNoteLength} characters and no control characters.");
        }

        if ((await store.ListAsync(workspaceId, ct)).Count >= AccessRuleRules.MaxRulesPerWorkspace)
        {
            throw new ArgumentException($"A workspace can have at most {AccessRuleRules.MaxRulesPerWorkspace} rules.");
        }

        var rule = await store.AddAsync(workspaceId, kind, normalized, cleanNote, time.GetUtcNow(), ct)
            ?? throw new ArgumentException("This workspace has that rule already.");
        await ChangedAsync(workspace.Name, workspaceId, "access_rule.added", rule, ct);
        return rule;
    }

    /// <summary>False when the rule no longer exists.</summary>
    public async Task<bool> SetEnabledAsync(long ruleId, bool enabled, CancellationToken ct)
    {
        var rule = await store.GetAsync(ruleId, ct);
        if (rule is null || !await store.SetEnabledAsync(ruleId, enabled, ct))
        {
            return false;
        }

        await ChangedAsync(await NameOfAsync(rule.WorkspaceId, ct), rule.WorkspaceId, enabled ? "access_rule.enabled" : "access_rule.disabled", rule, ct);
        return true;
    }

    /// <summary>False when the rule no longer exists.</summary>
    public async Task<bool> RemoveAsync(long ruleId, CancellationToken ct)
    {
        var rule = await store.GetAsync(ruleId, ct);
        if (rule is null || !await store.RemoveAsync(ruleId, ct))
        {
            return false;
        }

        await ChangedAsync(await NameOfAsync(rule.WorkspaceId, ct), rule.WorkspaceId, "access_rule.removed", rule, ct);
        return true;
    }

    private async Task<string> NameOfAsync(long workspaceId, CancellationToken ct) =>
        (await FindWorkspaceAsync(workspaceId, ct))?.Name ?? workspaceId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task ChangedAsync(string workspaceName, long workspaceId, string action, AccessRule rule, CancellationToken ct)
    {
        cache.Invalidate(workspaceId);
        await audit.AppendAsync(
            AuditEntries.Create(time, AuditCategory.Config, action, AuthDefaults.AdminActor, $"{workspaceName}: {rule.Kind} {rule.Pattern}"),
            ct);
        await events.PublishAsync(new AccessRulesChanged(workspaceId));
    }
}
