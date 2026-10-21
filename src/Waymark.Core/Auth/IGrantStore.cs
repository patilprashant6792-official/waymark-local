namespace Waymark.Core.Auth;

/// <summary>
/// Authorization codes and the access/refresh pairs issued from them. Only SHA-256 hashes are stored; every method that
/// changes more than one row does so in one transaction.
/// </summary>
public interface IGrantStore
{
    /// <summary>
    /// Marks the code as used. Consumed is returned exactly once; a second attempt returns AlreadyConsumed together with the
    /// code, so the caller can revoke everything that was issued from it.
    /// </summary>
    Task<CodeConsumeResult> ConsumeCodeAsync(byte[] codeHash, DateTimeOffset now, CancellationToken ct);

    /// <summary>Stores a new access and refresh pair that belong to the same family.</summary>
    Task IssuePairAsync(NewToken access, NewToken refresh, CancellationToken ct);

    /// <summary>The access token record when it exists, is not revoked and has not expired; otherwise null.</summary>
    Task<StoredToken?> FindActiveAccessAsync(byte[] hash, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Exchanges a refresh token for a new pair. Each refresh token works once: presenting a used one revokes the whole
    /// family and returns ReuseDetected.
    /// </summary>
    Task<RotationResult> RotateRefreshAsync(RotationRequest request, DateTimeOffset now, CancellationToken ct);

    Task<int> RevokeFamilyAsync(string familyId, DateTimeOffset now, CancellationToken ct);

    Task<int> RevokeByApprovalAsync(long approvalId, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Revokes the presented token for the given client (a refresh token revokes its whole family). False when it is not
    /// found for that client.
    /// </summary>
    Task<bool> RevokeTokenAsync(byte[] hash, string clientId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Deletes tokens and codes that expired before the cutoff.</summary>
    Task<int> PruneExpiredAsync(DateTimeOffset expiredBefore, CancellationToken ct);
}
