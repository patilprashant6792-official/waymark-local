namespace Waymark.Core.Audit;

/// <summary>Short constructor for the common audit entry, stamped with the injected clock.</summary>
public static class AuditEntries
{
    public static AuditEntry Create(
        TimeProvider time,
        AuditCategory category,
        string action,
        string? actor = null,
        string? target = null,
        AuditOutcome outcome = AuditOutcome.Ok,
        string? args = null,
        string? error = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        return new AuditEntry(time.GetUtcNow(), category, actor, action, target, args, null, null, outcome, error);
    }
}
