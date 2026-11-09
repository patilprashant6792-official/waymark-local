using Waymark.Core.Auth;

namespace Waymark.Tests.Auth;

public sealed class RandomValuesTests
{
    private const string PairingAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    [Theory]
    [InlineData(16, 22)]
    [InlineData(32, 43)]
    public void Base64UrlStringHasTheEncodedLengthAndOnlyUrlSafeCharacters(int bytes, int expectedLength)
    {
        var value = RandomValues.Base64UrlString(bytes);

        Assert.Equal(expectedLength, value.Length);
        Assert.All(value, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_', $"unexpected '{c}'"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(-1)]
    public void LessThanSixteenBytesIsRefused(int bytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RandomValues.Base64UrlString(bytes));

    [Fact]
    public void ValuesDoNotRepeat()
    {
        var values = Enumerable.Range(0, 200).Select(_ => RandomValues.Base64UrlString(32)).ToHashSet();

        Assert.Equal(200, values.Count);
    }

    [Fact]
    public void PrefixedValueStartsWithThePrefixAndCarries256Bits()
    {
        var value = RandomValues.WithPrefix("sma_");

        Assert.StartsWith("sma_", value);
        Assert.Equal(4 + 43, value.Length);
    }

    [Fact]
    public void PrefixedValueHonoursAByteCount() =>
        Assert.Equal(3 + 22, RandomValues.WithPrefix("ab_", 16).Length);

    [Fact]
    public void PrefixIsRequired()
    {
        Assert.Throws<ArgumentNullException>(() => RandomValues.WithPrefix(null!));
        Assert.Throws<ArgumentException>(() => RandomValues.WithPrefix(string.Empty));
    }

    [Fact]
    public void HexIsLowerCaseTwoCharactersPerByte()
    {
        var value = RandomValues.Hex(8);

        Assert.Equal(16, value.Length);
        Assert.Matches("^[0-9a-f]+$", value);
    }

    [Fact]
    public void PairingCodeUsesOnlyTheUnambiguousAlphabet()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = RandomValues.PairingCode();

            Assert.Equal(RandomValues.PairingCodeLength, code.Length);
            Assert.All(code, c => Assert.Contains(c, PairingAlphabet));
        }
    }

    [Fact]
    public void PairingAlphabetLeavesOutLookAlikes() =>
        Assert.DoesNotContain(PairingAlphabet, c => c is '0' or 'O' or '1' or 'I' or 'L');

    [Theory]
    [InlineData("K7Q4MD", "K7Q-4MD")]
    [InlineData("ABC", "ABC")]
    [InlineData("", "")]
    [InlineData("ABCDEFG", "ABCDEFG")]
    public void FormatPairingCodeSplitsOnlyAFullCode(string code, string expected) =>
        Assert.Equal(expected, RandomValues.FormatPairingCode(code));
}
