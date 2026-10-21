namespace Waymark.Core.Audit;

public enum AuditCategory
{
    Auth,
    Approval,
    Config,
    Tool
}

public enum AuditOutcome
{
    Ok,
    Denied,
    Error
}

/// <summary>
/// One audit row. ResultBytes is the size of what a tool sent back; ResultTokens is how many tokens that was for the AI
/// (null on rows recorded before tokens were counted).
/// </summary>
public sealed record AuditEntry(
    DateTimeOffset TimestampUtc,
    AuditCategory Category,
    string? Actor,
    string Action,
    string? Target,
    string? ArgsRedacted,
    int? DurationMs,
    int? ResultBytes,
    AuditOutcome Outcome,
    string? Error,
    int? ResultTokens = null);

public sealed record StoredAuditEntry(long Id, AuditEntry Entry);

/// <summary>
/// Newest-first query. BeforeId pages towards older entries and AfterId towards newer ones. Action is an exact name (a tool name for
/// tool calls), Target an exact value (the workspace), Text a case-insensitive search over action, actor, target and error.
/// </summary>
public sealed record AuditQuery(
    AuditCategory? Category = null,
    AuditOutcome? Outcome = null,
    long? BeforeId = null,
    int Limit = 50,
    string? Action = null,
    string? Target = null,
    string? Text = null,
    DateTimeOffset? SinceUtc = null,
    long? AfterId = null);
