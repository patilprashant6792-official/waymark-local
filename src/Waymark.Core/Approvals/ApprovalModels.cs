namespace Waymark.Core.Approvals;

public enum ApprovalStatus
{
    Pending,
    Approved,
    Denied,
    Expired
}

/// <summary>PollHash is the SHA-256 of the secret the pairing page polls with.</summary>
public sealed record NewApproval(
    byte[] PollHash,
    string PairingCode,
    string ClientId,
    string ClientNameClaimed,
    string RedirectUri,
    string RedirectHost,
    string? State,
    string CodeChallenge,
    string Scope,
    string Resource,
    DateTimeOffset RequestedUtc,
    DateTimeOffset ExpiresUtc);

public sealed record Approval(
    long Id,
    string PairingCode,
    string ClientId,
    string ClientNameClaimed,
    string RedirectUri,
    string RedirectHost,
    string? State,
    string CodeChallenge,
    string Scope,
    string Resource,
    DateTimeOffset RequestedUtc,
    DateTimeOffset ExpiresUtc,
    ApprovalStatus Status,
    DateTimeOffset? DecidedUtc,
    string? DecidedBy,
    string? DecisionReason,
    DateTimeOffset? CodeIssuedUtc);

public enum CodeMintStatus
{
    NotFound,
    Pending,
    Denied,
    Expired,
    AlreadyIssued,
    Issued
}

public sealed record CodeMintResult(CodeMintStatus Status, Approval? Approval);
