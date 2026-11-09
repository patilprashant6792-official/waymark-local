using Waymark.Core.Guides;

namespace Waymark.Tests.Guides;

public sealed class GuideRulesTests
{
    [Fact]
    public void FixedNamesAreWhatToolsAndResourcesRelyOn()
    {
        Assert.Equal("guide", GuideRules.ArgumentName);
        Assert.Equal("waymark_guide", GuideRules.OverviewTool);
        Assert.Equal("Waymark.Guides.", GuideRules.ResourcePrefix);
        Assert.Equal(".md", GuideRules.ResourceSuffix);
        Assert.Equal("host", HostPromptRules.StoreKey);
    }

    [Fact]
    public void ServerInstructionsStayShortAndPointToTheOverviewFirst()
    {
        Assert.True(GuideRules.ServerInstructions.Length < 4_000, "Claude Code cuts long server instructions at about 4,000 characters.");
        Assert.StartsWith("Waymark gives you", GuideRules.ServerInstructions);
        Assert.Contains(GuideRules.OverviewTool, GuideRules.ServerInstructions);
        Assert.True(GuideRules.ServerInstructions.IndexOf(GuideRules.OverviewTool, StringComparison.Ordinal)
            < GuideRules.ServerInstructions.IndexOf("recall", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("  \n text \n\n", "text")]
    [InlineData("\r\n\r\nbody\r\n", "body")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void NormalizeMakesLineEndingsUniformAndTrimsTheOuterSpace(string text, string expected) =>
        Assert.Equal(expected, GuideRules.Normalize(text));

    [Fact]
    public void EqualTextAfterNormalizingHasOneHash() =>
        Assert.Equal(GuideRules.Hash(GuideRules.Normalize("a\r\nb\r\n")), GuideRules.Hash(GuideRules.Normalize("a\nb")));

    [Fact]
    public void HashIsTheFirstTwelveCharactersOfTheSha256InLowerCase() =>
        Assert.Equal("ba7816bf8f01", GuideRules.Hash("abc"));

    [Fact]
    public void HashIsStableAndSensitiveToChanges()
    {
        Assert.Equal(GuideRules.Hash("text"), GuideRules.Hash("text"));
        Assert.NotEqual(GuideRules.Hash("text"), GuideRules.Hash("text."));
        Assert.Equal(12, GuideRules.Hash(string.Empty).Length);
    }

    [Fact]
    public void NullTextIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => GuideRules.Normalize(null!));
        Assert.Throws<ArgumentNullException>(() => GuideRules.Hash(null!));
    }

    [Theory]
    [InlineData("host", true)]
    [InlineData("Host", false)]
    [InlineData("code", false)]
    [InlineData("", false)]
    public void OnlyTheHostModuleIsTheOverview(string module, bool expected) =>
        Assert.Equal(expected, GuideRules.IsOverview(module));

    [Fact]
    public void PropertyDescriptionNamesTheModule()
    {
        Assert.Equal("true: put the Waymark overview first in the result. Prefer the waymark_guide tool.", GuideRules.PropertyDescription("host"));
        Assert.Equal(
            "true: put the files guide first in the result. Use on your first files call, or when told it changed.",
            GuideRules.PropertyDescription("files"));
    }

    [Fact]
    public void DeliverWrapsAModuleGuide() =>
        Assert.Equal(
            "[Waymark guide: code tools]\n" +
            "Sent once. Leave `guide` out of code tool calls from now on, unless a result says this guide changed.\n" +
            "---\n" +
            "BODY\n" +
            "[End of Waymark guide]",
            GuideRules.Deliver("code", "BODY"));

    [Fact]
    public void DeliverWrapsTheOverviewWithItsOwnWording()
    {
        var text = GuideRules.Deliver("host", "BODY");

        Assert.StartsWith("[Waymark guide: overview]\nSent once. Call waymark_guide again", text);
        Assert.Contains("\n---\nBODY\n", text);
        Assert.EndsWith("\n[End of Waymark guide]", text);
    }

    [Fact]
    public void DeliverRefusesBadArguments()
    {
        Assert.ThrowsAny<ArgumentException>(() => GuideRules.Deliver(" ", "x"));
        Assert.ThrowsAny<ArgumentException>(() => GuideRules.Deliver(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => GuideRules.Deliver("code", null!));
    }

    [Fact]
    public void NoticeTellsAModuleClientWhatToDo()
    {
        Assert.Equal(
            "Waymark has a usage guide for its code tools that you have not loaded. Add guide: true to your next code tool call to receive it once.",
            GuideRules.Notice("code", neverLoaded: true));
        Assert.Equal(
            "The Waymark code guide changed since you last loaded it. Add guide: true to your next code tool call to receive the new version.",
            GuideRules.Notice("code", neverLoaded: false));
    }

    [Fact]
    public void NoticeTellsAClientToCallTheOverviewTool()
    {
        Assert.Equal(
            "Waymark has an overview guide that you have not loaded. Call waymark_guide once to receive it.",
            GuideRules.Notice("host", neverLoaded: true));
        Assert.Equal(
            "The Waymark overview changed since you last loaded it. Call waymark_guide to receive the new version.",
            GuideRules.Notice("host", neverLoaded: false));
    }

    [Fact]
    public void NoticeRefusesABlankModule() => Assert.ThrowsAny<ArgumentException>(() => GuideRules.Notice("", neverLoaded: true));
}
