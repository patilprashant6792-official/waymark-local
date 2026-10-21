namespace Waymark.Core.Auth;

public sealed record AccessKey(
    string Id,
    string Name,
    string Hint,
    string Scope,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ExpiresUtc,
    DateTimeOffset? LastUsedUtc,
    DateTimeOffset? RevokedUtc)
{
    public bool IsRevoked => RevokedUtc is not null;

    public bool IsExpired(DateTimeOffset now) => ExpiresUtc is { } expires && expires <= now;
}

/// <param name="Hash">SHA-256 of the key; the key itself is shown once and never stored.</param>
/// <param name="Hint">The last characters of the key, to tell keys apart in the list.</param>
public sealed record NewAccessKey(
    string Id,
    string Name,
    byte[] Hash,
    string Hint,
    string Scope,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ExpiresUtc);

/// <summary>Long-lived keys for clients that cannot do the browser sign-in (scripts, other tools).</summary>
public interface IAccessKeyStore
{
    Task CreateAsync(NewAccessKey key, CancellationToken ct);

    /// <summary>The key when it exists, is not revoked and has not expired; otherwise null.</summary>
    Task<AccessKey?> FindActiveAsync(byte[] hash, DateTimeOffset now, CancellationToken ct);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<AccessKey>> ListAsync(CancellationToken ct);

    /// <summary>False when the key is unknown or already revoked.</summary>
    Task<bool> RevokeAsync(string id, DateTimeOffset now, CancellationToken ct);

    Task TouchAsync(string id, DateTimeOffset at, CancellationToken ct);
}
