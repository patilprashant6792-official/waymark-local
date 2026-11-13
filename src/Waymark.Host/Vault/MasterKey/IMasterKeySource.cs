namespace Waymark.Host.Vault.MasterKey;

/// <summary>The 256-bit key that encrypts the Data Protection key ring on macOS.</summary>
public interface IMasterKeySource
{
    /// <exception cref="System.Security.Cryptography.CryptographicException">The key cannot be read or created.</exception>
    ReadOnlyMemory<byte> Get();
}
