namespace Waymark.Core.Vault;

/// <summary>
/// The ASP.NET Data Protection key ring kept in the database (macOS): one entry per key element, stored exactly as Data Protection hands it
/// over, which is already encrypted. Entries are only ever added; Data Protection marks old keys as revoked or expired inside new elements.
/// </summary>
public interface IDataProtectionKeyStore
{
    /// <summary>Every stored element, oldest first.</summary>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken ct);

    Task AddAsync(string friendlyName, string xml, DateTimeOffset now, CancellationToken ct);
}
