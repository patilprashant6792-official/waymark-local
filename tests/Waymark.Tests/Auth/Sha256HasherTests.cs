using Waymark.Core.Auth;

namespace Waymark.Tests.Auth;

public sealed class Sha256HasherTests
{
    [Fact]
    public void HashMatchesTheKnownVectorForAbc() =>
        Assert.Equal(
            "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
            Convert.ToHexString(Sha256Hasher.Hash("abc")));

    [Fact]
    public void HashIsDeterministicAndThirtyTwoBytes()
    {
        var first = Sha256Hasher.Hash("some-token");
        var second = Sha256Hasher.Hash("some-token");

        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentValuesGiveDifferentHashes() =>
        Assert.NotEqual(Sha256Hasher.Hash("token-a"), Sha256Hasher.Hash("token-b"));

    [Fact]
    public void HashIsCaseSensitive() =>
        Assert.NotEqual(Sha256Hasher.Hash("Token"), Sha256Hasher.Hash("token"));

    [Fact]
    public void NullOrEmptyIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => Sha256Hasher.Hash(null!));
        Assert.Throws<ArgumentException>(() => Sha256Hasher.Hash(string.Empty));
    }
}
