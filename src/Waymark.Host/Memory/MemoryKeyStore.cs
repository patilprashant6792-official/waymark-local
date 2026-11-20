using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Waymark.Core.Memory;
using Waymark.Core.Settings;

namespace Waymark.Host.Memory;

/// <summary>
/// The embedding providers' API keys, one setting per provider ('memory.key.voyage'), each encrypted with ASP.NET Data Protection under its own purpose
/// ('Waymark.Memory.Key.voyage.v1'), exactly like the ngrok authtoken. A key that can no longer be decrypted (the key ring changed) reads as absent and must be entered again.
/// </summary>
public sealed class MemoryKeyStore(IConfigStore config, IDataProtectionProvider protection, ILogger<MemoryKeyStore> logger)
{
    private const int HintLength = 4;
    private const int MaxKeyChars = 512;

    public async Task<string?> GetAsync(string provider, CancellationToken ct)
    {
        var (value, _) = await ReadAsync(provider, ct);
        return value;
    }

    public async Task<MemoryKeyInfo> GetInfoAsync(string provider, CancellationToken ct)
    {
        var (value, unreadable) = await ReadAsync(provider, ct);
        return value is null
            ? new MemoryKeyInfo(provider, false, null, unreadable)
            : new MemoryKeyInfo(provider, true, value.Length > HintLength * 2 ? value[^HintLength..] : null, false);
    }

    /// <exception cref="ArgumentException">Empty, too long, or holding white space or control characters.</exception>
    public async Task SetAsync(string provider, string key, string changedBy, CancellationToken ct)
    {
        var clean = (key ?? string.Empty).Trim();
        if (clean.Length == 0 || clean.Length > MaxKeyChars || clean.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new ArgumentException("Paste the API key as one piece of text, without spaces or line breaks.", nameof(key));
        }

        var json = JsonSerializer.Serialize<string?>(Protector(provider).Protect(clean));
        await config.SetAsync(SettingScope.Global, SettingKeys.MemoryKey(provider), json, changedBy, ct);
    }

    public Task ClearAsync(string provider, string changedBy, CancellationToken ct) =>
        config.SetAsync(SettingScope.Global, SettingKeys.MemoryKey(provider), JsonSerializer.Serialize<string?>(null), changedBy, ct);

    private async Task<(string? Value, bool Unreadable)> ReadAsync(string provider, CancellationToken ct)
    {
        var setting = await config.GetAsync(SettingScope.Global, SettingKeys.MemoryKey(provider), ct);
        var protectedValue = setting is null ? null : JsonSerializer.Deserialize<string?>(setting.ValueJson);
        if (string.IsNullOrEmpty(protectedValue))
        {
            return (null, false);
        }

        try
        {
            return (Protector(provider).Unprotect(protectedValue), false);
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "The saved {Provider} key for search by meaning cannot be decrypted (the key ring changed); it must be entered again.", provider);
            return (null, true);
        }
    }

    private IDataProtector Protector(string provider) => protection.CreateProtector($"Waymark.Memory.Key.{provider}.v1");
}
