using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Waymark.Core.Auth;

namespace Waymark.Host.Auth;

/// <summary>Which redirect addresses a client may register, and when a requested one counts as registered.</summary>
public static class RedirectUriPolicy
{
    public const string HostedCallback = "https://claude.ai/api/mcp/auth_callback";

    public static bool IsAllowedForRegistration(string? uri) => uri == HostedCallback || TryParseLoopbackCallback(uri, out _);

    /// <summary>An exact match, or for a loopback callback the same host and path on any port (RFC 8252).</summary>
    public static bool Matches(IReadOnlyList<string> registered, string? requested)
    {
        ArgumentNullException.ThrowIfNull(registered);
        if (string.IsNullOrEmpty(requested))
        {
            return false;
        }

        if (registered.Contains(requested, StringComparer.Ordinal))
        {
            return true;
        }

        if (!TryParseLoopbackCallback(requested, out var wanted))
        {
            return false;
        }

        foreach (var candidate in registered)
        {
            if (TryParseLoopbackCallback(candidate, out var have) && have.Host == wanted.Host)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseLoopbackCallback(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrEmpty(value) || !Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        var isLoopbackHost = parsed.Host is "localhost" or "127.0.0.1";
        if (parsed.Scheme != Uri.UriSchemeHttp || !isLoopbackHost || parsed.AbsolutePath != "/callback"
            || parsed.Query.Length != 0 || parsed.Fragment.Length != 0 || parsed.UserInfo.Length != 0)
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}

/// <summary>PKCE with the S256 method only.</summary>
public static class Pkce
{
    public static bool IsValidChallenge(string? value) => IsWellFormed(value);

    public static bool Verify(string? verifier, string challenge)
    {
        if (!IsWellFormed(verifier) || !IsWellFormed(challenge))
        {
            return false;
        }

        var computed = Encoding.ASCII.GetBytes(Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier!))));
        var expected = Encoding.ASCII.GetBytes(challenge);
        return computed.Length == expected.Length && CryptographicOperations.FixedTimeEquals(computed, expected);
    }

    private static bool IsWellFormed(string? value) =>
        value is { Length: >= 43 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~');
}

public static class OAuthScopes
{
    private static readonly string[] Supported = [AuthDefaults.McpScope, AuthDefaults.OfflineAccessScope];

    /// <summary>An empty request means mcp; mcp is always granted; unknown scopes are refused.</summary>
    public static bool TryNormalize(string? requested, out string normalized)
    {
        string[] parts = string.IsNullOrWhiteSpace(requested)
            ? [AuthDefaults.McpScope]
            : requested.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Any(p => !Supported.Contains(p)))
        {
            normalized = string.Empty;
            return false;
        }

        var granted = parts.ToHashSet();
        granted.Add(AuthDefaults.McpScope);
        normalized = string.Join(' ', Supported.Where(granted.Contains));
        return true;
    }

    public static bool Includes(string scope, string wanted) =>
        scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(wanted);
}
