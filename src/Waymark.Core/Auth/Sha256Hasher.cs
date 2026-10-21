using System.Security.Cryptography;
using System.Text;

namespace Waymark.Core.Auth;

/// <summary>
/// Codes, tokens, API keys and polling secrets are stored only as this SHA-256 hash. The values are long random
/// strings, so a plain fast hash is enough and an indexed lookup by hash is possible.
/// </summary>
public static class Sha256Hasher
{
    public static byte[] Hash(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return SHA256.HashData(Encoding.UTF8.GetBytes(value));
    }
}
