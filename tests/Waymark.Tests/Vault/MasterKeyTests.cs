using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Waymark.Data.Stores;
using Waymark.Host.Vault.MasterKey;
using Waymark.Tests.Support;

namespace Waymark.Tests.Vault;

/// <summary>
/// The macOS key protection, run on any system with a master key held in memory: the Keychain itself is only reachable on a Mac.
/// </summary>
public sealed class MasterKeyTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public void SealedBytesOpenWithTheSameKey()
    {
        var key = RandomNumberGenerator.GetBytes(MasterKeyCipher.KeyBytes);
        var plaintext = Encoding.UTF8.GetBytes("<key>secret material</key>");

        var sealedBytes = MasterKeyCipher.Seal(key, plaintext);

        Assert.Equal(plaintext, MasterKeyCipher.Open(key, sealedBytes));
        Assert.Equal(12 + 16 + plaintext.Length, sealedBytes.Length);
    }

    [Fact]
    public void EverySealUsesAFreshNonce()
    {
        var key = RandomNumberGenerator.GetBytes(MasterKeyCipher.KeyBytes);
        var plaintext = Encoding.UTF8.GetBytes("same");

        Assert.NotEqual(MasterKeyCipher.Seal(key, plaintext), MasterKeyCipher.Seal(key, plaintext));
    }

    [Fact]
    public void AnotherKeyCannotOpenIt()
    {
        var sealedBytes = MasterKeyCipher.Seal(RandomNumberGenerator.GetBytes(MasterKeyCipher.KeyBytes), [1, 2, 3]);

        Assert.ThrowsAny<CryptographicException>(() => MasterKeyCipher.Open(RandomNumberGenerator.GetBytes(MasterKeyCipher.KeyBytes), sealedBytes));
    }

    [Fact]
    public void AChangedByteIsDetected()
    {
        var key = RandomNumberGenerator.GetBytes(MasterKeyCipher.KeyBytes);
        var sealedBytes = MasterKeyCipher.Seal(key, [1, 2, 3]);
        sealedBytes[^1] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => MasterKeyCipher.Open(key, sealedBytes));
    }

    [Fact]
    public void ATooShortBlobOrAWrongSizedKeyIsRefused()
    {
        var key = RandomNumberGenerator.GetBytes(MasterKeyCipher.KeyBytes);

        Assert.Throws<CryptographicException>(() => MasterKeyCipher.Open(key, new byte[10]));
        Assert.Throws<CryptographicException>(() => MasterKeyCipher.Seal(new byte[16], [1]));
    }

    [Fact]
    public void AnElementSurvivesEncryptionAndHidesItsContent()
    {
        var masterKey = new FixedMasterKey();
        var element = new XElement("descriptor", new XElement("masterKey", "c2VjcmV0LXZhbHVl"));

        var encrypted = new MasterKeyXmlEncryptor(masterKey).Encrypt(element);
        var decrypted = new MasterKeyXmlDecryptor(Provider(masterKey)).Decrypt(encrypted.EncryptedElement);

        Assert.Equal(typeof(MasterKeyXmlDecryptor), encrypted.DecryptorType);
        Assert.DoesNotContain("c2VjcmV0LXZhbHVl", encrypted.EncryptedElement.ToString(), StringComparison.Ordinal);
        Assert.True(XNode.DeepEquals(element, decrypted));
    }

    [Fact]
    public void AForeignElementIsRefused()
    {
        var decryptor = new MasterKeyXmlDecryptor(Provider(new FixedMasterKey()));

        Assert.Throws<CryptographicException>(() => decryptor.Decrypt(new XElement("somethingElse")));
        Assert.Throws<CryptographicException>(() => decryptor.Decrypt(new XElement("encryptedWithWaymarkMasterKey", new XAttribute("version", 1), new XElement("value", "not base64!"))));
    }

    [Fact]
    public async Task TheKeyRingInTheDatabaseOpensAgainAfterARestart()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var masterKey = new FixedMasterKey();
        string protectedText;
        using (var first = DataProtection(db, masterKey))
        {
            protectedText = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("hello");
        }

        using var second = DataProtection(db, masterKey);

        Assert.Equal("hello", second.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedText));
        var stored = Assert.Single(await new DataProtectionKeyStore(db.Connections, db.Writes).ListAsync(_ct));
        Assert.Contains("encryptedWithWaymarkMasterKey", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("<masterKey", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACopiedDatabaseWithoutTheMasterKeyOpensNothing()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        string protectedText;
        using (var original = DataProtection(db, new FixedMasterKey()))
        {
            protectedText = original.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("hello");
        }

        using var elsewhere = DataProtection(db, new FixedMasterKey());

        Assert.ThrowsAny<CryptographicException>(() =>
            elsewhere.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedText));
    }

    private static ServiceProvider Provider(IMasterKeySource masterKey) =>
        new ServiceCollection().AddSingleton(masterKey).BuildServiceProvider();

    // The same wiring as AddWaymarkDataProtection on macOS, with the Keychain replaced by a key in memory.
    private static ServiceProvider DataProtection(TestDatabase db, IMasterKeySource masterKey)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(masterKey);
        services.AddDataProtection().SetApplicationName("Waymark");
        services.Configure<KeyManagementOptions>(options =>
        {
            options.XmlRepository = new SqliteXmlRepository(new DataProtectionKeyStore(db.Connections, db.Writes), TimeProvider.System);
            options.XmlEncryptor = new MasterKeyXmlEncryptor(masterKey);
        });
        return services.BuildServiceProvider();
    }

    private sealed class FixedMasterKey : IMasterKeySource
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(MasterKeyCipher.KeyBytes);

        public ReadOnlyMemory<byte> Get() => _key;
    }
}
