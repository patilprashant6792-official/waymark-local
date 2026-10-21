using System.Buffers.Text;
using System.Security.Cryptography;

namespace Waymark.Core.Auth;

/// <summary>Cryptographically random values for codes, tokens, keys and ids.</summary>
public static class RandomValues
{
    /// <summary>No 0/O or 1/I/L, so a code read aloud or compared by eye is unambiguous.</summary>
    private const string PairingAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public const int PairingCodeLength = 6;

    public static string Base64UrlString(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(byteCount, 16);
        return Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(byteCount));
    }

    /// <summary>A prefixed value with 256 bits of randomness, such as sma_...</summary>
    public static string WithPrefix(string prefix, int byteCount = 32)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        return prefix + Base64UrlString(byteCount);
    }

    public static string Hex(int byteCount) => Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();

    public static string PairingCode() =>
        new(RandomNumberGenerator.GetItems<char>(PairingAlphabet, PairingCodeLength));

    /// <summary>K7Q4MD becomes K7Q-4MD for display.</summary>
    public static string FormatPairingCode(string code) =>
        code.Length == PairingCodeLength ? string.Concat(code.AsSpan(0, 3), "-", code.AsSpan(3)) : code;
}
