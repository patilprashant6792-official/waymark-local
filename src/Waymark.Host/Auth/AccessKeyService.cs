using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Events;

namespace Waymark.Host.Auth;

/// <param name="Value">The key as the person copies it. It exists only in this result; Waymark keeps just its hash.</param>
public sealed record CreatedAccessKey(AccessKey Key, string Value);

/// <summary>Creates and revokes API keys, drops cached credentials on revoke and records both in the audit trail.</summary>
public sealed class AccessKeyService(IAccessKeyStore keys, IAuditStore audit, IEventBus events, AuthCache cache, TimeProvider time)
{
    public const int MaxNameLength = 60;
    private const int HintLength = 4;

    public async Task<CreatedAccessKey> CreateAsync(string name, TimeSpan? lifetime, CancellationToken ct)
    {
        var cleaned = CleanName(name);
        if (cleaned.Length == 0)
        {
            throw new ArgumentException("A name is required, so the key can be recognised later.", nameof(name));
        }

        if (lifetime is { } span && span <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "The lifetime must be positive.");
        }

        var now = time.GetUtcNow();
        var value = RandomValues.WithPrefix(AuthDefaults.ApiKeyPrefix);
        var key = new NewAccessKey(
            "key_" + RandomValues.Hex(8),
            cleaned,
            Sha256Hasher.Hash(value),
            value[^HintLength..],
            AuthDefaults.McpScope,
            now,
            lifetime is null ? null : now + lifetime);
        await keys.CreateAsync(key, ct);

        await audit.AppendAsync(AuditEntries.Create(time, AuditCategory.Auth, "apikey.created", AuthDefaults.AdminActor, key.Id, args: cleaned), ct);
        return new CreatedAccessKey(new AccessKey(key.Id, key.Name, key.Hint, key.Scope, now, key.ExpiresUtc, null, null), value);
    }

    public async Task<bool> RevokeAsync(string id, CancellationToken ct)
    {
        var revoked = await keys.RevokeAsync(id, time.GetUtcNow(), ct);
        cache.Invalidate();
        await audit.AppendAsync(
            AuditEntries.Create(
                time,
                AuditCategory.Auth,
                "apikey.revoked",
                AuthDefaults.AdminActor,
                id,
                revoked ? AuditOutcome.Ok : AuditOutcome.Error,
                error: revoked ? null : "Unknown or already revoked."),
            ct);

        if (revoked)
        {
            await events.PublishAsync(new CredentialsRevoked());
        }

        return revoked;
    }

    private static string CleanName(string? name)
    {
        var cleaned = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length <= MaxNameLength ? cleaned : cleaned[..MaxNameLength];
    }
}
