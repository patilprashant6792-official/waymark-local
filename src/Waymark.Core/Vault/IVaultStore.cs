namespace Waymark.Core.Vault;

/// <summary>A secret as listed in the admin UI. The value is never part of it.</summary>
/// <param name="Hint">The last characters of the value, so the owner can tell secrets apart.</param>
public sealed record VaultSecret(
    long Id,
    string Name,
    string Hint,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? LastUsedUtc);

/// <summary>Stores secrets that are already encrypted. Encryption is the caller's job.</summary>
public interface IVaultStore
{
    Task<IReadOnlyList<VaultSecret>> ListAsync(CancellationToken ct);

    /// <summary>The encrypted value, or null when no secret has this name.</summary>
    Task<string?> GetProtectedAsync(string name, CancellationToken ct);

    /// <summary>Every encrypted value with its name, for scrubbing output.</summary>
    Task<IReadOnlyList<KeyValuePair<string, string>>> ListProtectedAsync(CancellationToken ct);

    /// <summary>Creates the secret or replaces its value. Returns true when it was created.</summary>
    Task<bool> UpsertAsync(string name, string protectedValue, string hint, DateTimeOffset now, CancellationToken ct);

    Task<bool> MarkUsedAsync(string name, DateTimeOffset now, CancellationToken ct);

    /// <summary>False when no secret has this name.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken ct);
}
