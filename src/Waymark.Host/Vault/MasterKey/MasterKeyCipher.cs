using System.Security.Cryptography;
using System.Text;

namespace Waymark.Host.Vault.MasterKey;

/// <summary>
/// AES-256-GCM with a fresh 96-bit nonce per message. The sealed form is nonce (12 bytes), tag (16 bytes), then the ciphertext.
/// A fixed label is bound as associated data, so a blob made for another purpose with the same key does not open here.
/// </summary>
public static class MasterKeyCipher
{
    public const int KeyBytes = 32;

    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private static readonly byte[] Label = Encoding.ASCII.GetBytes("Waymark.DataProtection.KeyRing.v1");

    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext)
    {
        CheckKey(key);
        var sealedBytes = new byte[NonceBytes + TagBytes + plaintext.Length];
        var nonce = sealedBytes.AsSpan(0, NonceBytes);
        var tag = sealedBytes.AsSpan(NonceBytes, TagBytes);
        var ciphertext = sealedBytes.AsSpan(NonceBytes + TagBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Label);
        return sealedBytes;
    }

    /// <exception cref="CryptographicException">The blob is too short, was changed, or was sealed with another key.</exception>
    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> sealedBytes)
    {
        CheckKey(key);
        if (sealedBytes.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("The encrypted key element is too short.");
        }

        var plaintext = new byte[sealedBytes.Length - NonceBytes - TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(sealedBytes[..NonceBytes], sealedBytes[(NonceBytes + TagBytes)..], sealedBytes.Slice(NonceBytes, TagBytes), plaintext, Label);
        return plaintext;
    }

    private static void CheckKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyBytes)
        {
            throw new CryptographicException($"The master key must be {KeyBytes} bytes.");
        }
    }
}
