using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Waymark.Host.Vault.MasterKey;

/// <summary>Encrypts each Data Protection key element with the master key before it is stored.</summary>
public sealed class MasterKeyXmlEncryptor(IMasterKeySource masterKey) : IXmlEncryptor
{
    internal const string ElementName = "encryptedWithWaymarkMasterKey";
    internal const string ValueName = "value";
    internal const string VersionName = "version";
    internal const int Version = 1;

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        try
        {
            var sealedBytes = MasterKeyCipher.Seal(masterKey.Get().Span, plaintext);
            var element = new XElement(
                ElementName,
                new XAttribute(VersionName, Version),
                new XComment(" Encrypted with the Waymark master key kept in the macOS login Keychain. "),
                new XElement(ValueName, Convert.ToBase64String(sealedBytes)));
            return new EncryptedXmlInfo(element, typeof(MasterKeyXmlDecryptor));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

/// <summary>
/// Opens what <see cref="MasterKeyXmlEncryptor"/> wrote. Data Protection creates it by its type name from the stored XML, through the
/// service provider, so it takes the provider rather than the key source.
/// </summary>
public sealed class MasterKeyXmlDecryptor : IXmlDecryptor
{
    private readonly IMasterKeySource _masterKey;

    public MasterKeyXmlDecryptor(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _masterKey = services.GetRequiredService<IMasterKeySource>();
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        if (encryptedElement.Name != MasterKeyXmlEncryptor.ElementName
            || (int?)encryptedElement.Attribute(MasterKeyXmlEncryptor.VersionName) != MasterKeyXmlEncryptor.Version)
        {
            throw new CryptographicException("The key element was not written by this version of the Waymark key encryption.");
        }

        var text = encryptedElement.Element(MasterKeyXmlEncryptor.ValueName)?.Value
            ?? throw new CryptographicException("The encrypted key element has no value.");
        byte[] sealedBytes;
        try
        {
            sealedBytes = Convert.FromBase64String(text);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("The encrypted key element is not valid base64.", ex);
        }

        var plaintext = MasterKeyCipher.Open(_masterKey.Get().Span, sealedBytes);
        try
        {
            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
