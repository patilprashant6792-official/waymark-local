namespace Waymark.Core.Auth;

public sealed record OAuthClient(
    string ClientId,
    string ClientName,
    IReadOnlyList<string> RedirectUris,
    DateTimeOffset RegisteredUtc,
    DateTimeOffset? LastUsedUtc,
    DateTimeOffset? RevokedUtc)
{
    public bool IsRevoked => RevokedUtc is not null;
}

public interface IOAuthClientStore
{
    /// <summary>Stores a newly registered client. A duplicate client id throws.</summary>
    Task AddAsync(OAuthClient client, CancellationToken ct);

    Task<OAuthClient?> GetAsync(string clientId, CancellationToken ct);

    Task<IReadOnlyList<OAuthClient>> ListAsync(CancellationToken ct);

    Task TouchAsync(string clientId, DateTimeOffset at, CancellationToken ct);

    /// <summary>Revokes the client and every token it holds in one transaction. False when unknown or already revoked.</summary>
    Task<bool> RevokeAsync(string clientId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Deletes clients registered before the cutoff that were never used and never asked for approval.</summary>
    Task<int> PruneUnusedAsync(DateTimeOffset registeredBefore, CancellationToken ct);

    /// <summary>
    /// Deletes every disconnected client together with its requests, authorization codes and tokens, in one transaction.
    /// An active client, and a disconnected one that still has a waiting request, is never deleted. Returns how many clients were removed.
    /// </summary>
    Task<int> ClearDisconnectedAsync(DateTimeOffset now, CancellationToken ct);
}
