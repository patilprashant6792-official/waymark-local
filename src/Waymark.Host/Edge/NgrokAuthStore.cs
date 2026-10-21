using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Waymark.Core.Edge;
using Waymark.Core.Settings;

namespace Waymark.Host.Edge;

/// <summary>
/// Saves the ngrok authtoken in the settings table, encrypted with ASP.NET Data Protection. The keys live next to
/// the database and are protected by Windows DPAPI, so a copied database file cannot be read on another account.
/// </summary>
public sealed class NgrokAuthStore(IConfigStore config, IDataProtectionProvider protection, ILogger<NgrokAuthStore> logger) : INgrokAuthStore
{
    private const string Purpose = "Waymark.Ngrok.Authtoken.v1";
    private const int HintLength = 4;

    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);

    public async Task<NgrokAuthInfo> GetInfoAsync(CancellationToken ct)
    {
        var value = await GetAsync(ct);
        return value is null
            ? new NgrokAuthInfo(false, null)
            : new NgrokAuthInfo(true, value.Length > HintLength ? value[^HintLength..] : null);
    }

    public async Task<string?> GetAsync(CancellationToken ct)
    {
        var setting = await config.GetAsync(SettingScope.Global, SettingKeys.NgrokSecret, ct);
        var protectedValue = setting is null ? null : JsonSerializer.Deserialize<string?>(setting.ValueJson);
        if (string.IsNullOrEmpty(protectedValue))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "The saved ngrok authtoken cannot be decrypted (the key ring changed); it must be entered again.");
            return null;
        }
    }

    public async Task SetAsync(string value, string changedBy, CancellationToken ct)
    {
        if (!NgrokAuthRules.TryNormalize(value, out var normalized, out var error))
        {
            throw new ArgumentException(error, nameof(value));
        }

        var json = JsonSerializer.Serialize<string?>(_protector.Protect(normalized));
        await config.SetAsync(SettingScope.Global, SettingKeys.NgrokSecret, json, changedBy, ct);
    }

    public Task ClearAsync(string changedBy, CancellationToken ct) =>
        config.SetAsync(SettingScope.Global, SettingKeys.NgrokSecret, JsonSerializer.Serialize<string?>(null), changedBy, ct);
}
