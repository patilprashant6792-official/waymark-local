namespace Waymark.Core.Approvals;

public interface IApprovalStore
{
    Task<Approval> CreateAsync(NewApproval request, CancellationToken ct);

    Task<Approval?> GetAsync(long id, CancellationToken ct);

    /// <summary>Read-only lookup for the polling browser; changes nothing.</summary>
    Task<Approval?> GetByPollHashAsync(byte[] pollHash, CancellationToken ct);

    Task<IReadOnlyList<Approval>> ListPendingAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<Approval>> ListRecentAsync(int limit, CancellationToken ct);

    /// <summary>Approves or denies an approval that is still pending and unexpired. False otherwise.</summary>
    Task<bool> DecideAsync(long id, bool approve, string decidedBy, string? reason, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Called by the polling browser. For an approved request that has not issued a code yet, stores the hash of a
    /// freshly generated one-time code (so the plaintext never touches the database) and returns Issued exactly once.
    /// </summary>
    Task<CodeMintResult> TryMintCodeAsync(byte[] pollHash, byte[] codeHash, DateTimeOffset codeExpiresUtc, DateTimeOffset now, CancellationToken ct);

    /// <summary>Marks pending approvals past their expiry as expired.</summary>
    Task<int> ExpireStaleAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>Marks every pending approval expired, for example at server start.</summary>
    Task<int> ExpireAllPendingAsync(DateTimeOffset now, string reason, CancellationToken ct);

    /// <summary>
    /// Deletes finished requests (denied, expired, or pending past their expiry) and approved ones whose app is no longer
    /// active. Waiting requests and approvals that belong to an app that is still connected are never deleted.
    /// Returns how many were removed.
    /// </summary>
    Task<int> ClearFinishedAsync(DateTimeOffset now, CancellationToken ct);
}
