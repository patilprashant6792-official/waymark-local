using Waymark.Core.Text;

namespace Waymark.Tests.Text;

public sealed class IdentifierWordsTests
{
    [Fact]
    public void SplitsACamelCaseNameIntoTheWholeAndItsParts()
    {
        var tokens = IdentifierWords.Tokenize("CompleteChatAsync").ToList();

        Assert.Equal(["completechatasync", "complete", "chat", "async"], tokens);
    }

    [Fact]
    public void SplitsSnakeCase()
    {
        var tokens = IdentifierWords.Tokenize("refresh_token").ToList();

        Assert.Equal(["refresh", "token"], tokens);
    }

    [Fact]
    public void KeepsAnAcronymTogether()
    {
        Assert.Equal(["http", "client2"], IdentifierWords.Parts("HTTPClient2").ToList());
    }

    [Fact]
    public void DropsSingleCharacterTokens()
    {
        Assert.DoesNotContain("a", IdentifierWords.Tokenize("A_b_Name"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingGivesNoTokens(string? value)
    {
        Assert.Empty(IdentifierWords.Tokenize(value));
    }

    [Fact]
    public void SanitizeKeepsLowerCaseLettersAndDigitsOnly()
    {
        Assert.Equal("userservice2", IdentifierWords.Sanitize("User_Service-2!"));
    }
}
