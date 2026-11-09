using Waymark.Host.Edge;

namespace Waymark.Tests.Edge;

public sealed class NgrokAuthRulesTests
{
    private static string Token(int length) => new('a', length);

    [Fact]
    public void AcceptsATokenOfLettersDigitsUnderscoresAndDashes()
    {
        var ok = NgrokAuthRules.TryNormalize("2abcDEF123_ghi-JKL456mno789", out var value, out var error);

        Assert.True(ok);
        Assert.Equal("2abcDEF123_ghi-JKL456mno789", value);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void TrimsSurroundingWhitespace()
    {
        var ok = NgrokAuthRules.TryNormalize("  " + Token(30) + "\r\n", out var value, out _);

        Assert.True(ok);
        Assert.Equal(Token(30), value);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(256)]
    public void TheLengthBoundsAreAllowed(int length)
    {
        Assert.True(NgrokAuthRules.TryNormalize(Token(length), out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    [InlineData(257)]
    public void ATokenOutsideTheLengthBoundsIsRefused(int length)
    {
        var ok = NgrokAuthRules.TryNormalize(Token(length), out var value, out var error);

        Assert.False(ok);
        Assert.Equal(string.Empty, value);
        Assert.Contains("authtoken", error, StringComparison.Ordinal);
    }

    [Fact]
    public void NullIsRefused() =>
        Assert.False(NgrokAuthRules.TryNormalize(null, out _, out _));

    [Theory]
    [InlineData("ngrok config add-authtoken 2abcDEF123_ghi-JKL456mno789")]
    [InlineData("2abcDEF123.ghi-JKL456mno789")]
    [InlineData("2abcDEF123 ghi-JKL456mno789")]
    [InlineData("2abcDEF123$ghi-JKL456mno789")]
    [InlineData("2abcDEF123_ghi-JKL456mno78\u00e9")]
    public void APastedCommandOrOddCharactersAreRefused(string input)
    {
        var ok = NgrokAuthRules.TryNormalize(input, out var value, out _);

        Assert.False(ok);
        Assert.Equal(string.Empty, value);
    }
}
