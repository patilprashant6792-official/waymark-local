namespace Waymark.Core.Auth;

public enum TokenKind
{
    Access,
    Refresh
}

public sealed record NewToken(
    byte[] Hash,
    TokenKind Kind,
    string FamilyId,
    string ClientId,
    long? ApprovalId,
    string Scope,
    string Resource,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc);

public sealed record StoredToken(
    TokenKind Kind,
    string FamilyId,
    string ClientId,
    long? ApprovalId,
    string Scope,
    string Resource,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset? UsedUtc,
    DateTimeOffset? RevokedUtc);

public sealed record StoredAuthCode(
    long ApprovalId,
    string ClientId,
    string RedirectUri,
    string CodeChallenge,
    string Scope,
    string Resource,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset? ConsumedUtc);

public enum CodeConsumeStatus
{
    Consumed,
    NotFound,
    Expired,
    AlreadyConsumed
}

public sealed record CodeConsumeResult(CodeConsumeStatus Status, StoredAuthCode? Code);

/// <summary>ClientId is the client presenting the refresh token; a token that belongs to another client is reported as NotFound.</summary>
public sealed record RotationRequest(
    string ClientId,
    byte[] PresentedRefreshHash,
    byte[] NewAccessHash,
    byte[] NewRefreshHash,
    DateTimeOffset AccessExpiresUtc,
    DateTimeOffset RefreshExpiresUtc);

public enum RotationStatus
{
    Rotated,
    NotFound,
    Expired,
    Revoked,
    ReuseDetected
}

/// <summary>Token is the presented refresh token's record (client, scope, resource) when it was found.</summary>
public sealed record RotationResult(RotationStatus Status, StoredToken? Token);
