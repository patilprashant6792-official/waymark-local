namespace Waymark.Core.Edge;

/// <param name="IsSet">A usable ngrok authtoken is saved.</param>
/// <param name="Hint">The last characters of the saved authtoken, enough to recognise it and nothing more.</param>
public sealed record NgrokAuthInfo(bool IsSet, string? Hint);

/// <summary>Keeps the ngrok authtoken. It is stored encrypted and is only ever handed back to the code that starts ngrok.</summary>
public interface INgrokAuthStore
{
    Task<NgrokAuthInfo> GetInfoAsync(CancellationToken ct);

    /// <summary>The plain authtoken, or null when none is saved or it can no longer be decrypted.</summary>
    Task<string?> GetAsync(CancellationToken ct);

    Task SetAsync(string value, string changedBy, CancellationToken ct);

    Task ClearAsync(string changedBy, CancellationToken ct);
}
