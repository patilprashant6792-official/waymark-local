using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Vault;

namespace Waymark.Host.Vault;

/// <summary>
/// Saves named secrets encrypted with ASP.NET Data Protection (keys next to the database, protected by Windows DPAPI).
/// A secret value is never returned to the admin UI and never written to the audit trail.
/// </summary>
public sealed class VaultService(
    IVaultStore store,
    IDataProtectionProvider protection,
    IAuditStore audit,
    TimeProvider time,
    ILogger<VaultService> logger)
{
    private const string Purpose = "Waymark.Vault.Secret.v1";

    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);

    public Task<IReadOnlyList<VaultSecret>> ListAsync(CancellationToken ct) => store.ListAsync(ct);

    /// <summary>Creates or replaces a secret. Returns true when it was created. Throws ArgumentException for a bad name or value.</summary>
    public async Task<bool> SaveAsync(string? name, string? value, CancellationToken ct)
    {
        if (!VaultRules.TryNormalize(name, value, out var normalizedName, out var error))
        {
            throw new ArgumentException(error);
        }

        var created = await store.UpsertAsync(normalizedName, _protector.Protect(value!), VaultRules.HintOf(value!), time.GetUtcNow(), ct);
        await audit.AppendAsync(
            AuditEntries.Create(time, AuditCategory.Config, created ? "vault.created" : "vault.replaced", AuthDefaults.AdminActor, normalizedName),
            ct);
        return created;
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        var deleted = await store.DeleteAsync(name, ct);
        if (deleted)
        {
            await audit.AppendAsync(AuditEntries.Create(time, AuditCategory.Config, "vault.deleted", AuthDefaults.AdminActor, name), ct);
        }

        return deleted;
    }

    /// <summary>The plain value, or null when the secret does not exist or cannot be decrypted. Marks the secret as used.</summary>
    public async Task<string?> ResolveAsync(string name, CancellationToken ct)
    {
        var protectedValue = await store.GetProtectedAsync(name, ct);
        if (protectedValue is null)
        {
            return null;
        }

        try
        {
            var value = _protector.Unprotect(protectedValue);
            await store.MarkUsedAsync(name, time.GetUtcNow(), ct);
            return value;
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "The secret {Name} cannot be decrypted (the key ring changed); it must be entered again.", name);
            return null;
        }
    }

    /// <summary>Replaces every stored secret value in the text with [secret:NAME]. Used on command output before it leaves the machine.</summary>
    public async Task<string> ScrubAsync(string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        var plain = new List<KeyValuePair<string, string>>();
        foreach (var item in await store.ListProtectedAsync(ct))
        {
            try
            {
                plain.Add(new KeyValuePair<string, string>(item.Key, _protector.Unprotect(item.Value)));
            }
            catch (CryptographicException ex)
            {
                logger.LogWarning(ex, "The secret {Name} cannot be decrypted and is skipped while cleaning output.", item.Key);
            }
        }

        return VaultRules.Scrub(text, plain);
    }
}
