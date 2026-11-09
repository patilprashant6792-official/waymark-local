using Waymark.Core.Guides;

namespace Waymark.Tests.Guides;

public sealed class HostTemplateTests
{
    private const string Template = "A\n{{owner}}\nB\n{{setup}}\nC";

    [Fact]
    public void TheTemplateIsCutIntoThreeParts()
    {
        var template = HostTemplate.Parse(Template);

        Assert.Equal("A\n", template.BeforeOwner);
        Assert.Equal("B\n", template.BetweenOwnerAndSetup);
        Assert.Equal("\nC", template.AfterSetup);
    }

    [Fact]
    public void WindowsLineBreaksAfterAMarkerAreDropped()
    {
        var template = HostTemplate.Parse("A\r\n{{owner}}\r\nB\r\n{{setup}}\r\nC");

        Assert.Equal("B\r\n", template.BetweenOwnerAndSetup);
    }

    [Fact]
    public void RenderPlacesTheOwnerBlockAndTheSetup()
    {
        var rendered = HostTemplate.Parse(Template).Render("OWNER\n\n", "SETUP");

        Assert.Equal("A\nOWNER\n\nB\nSETUP\nC", rendered);
    }

    [Fact]
    public void StableLeavesOutTheLiveSetup()
    {
        var template = HostTemplate.Parse(Template);

        var stable = template.Stable("OWNER");

        Assert.Contains("OWNER", stable);
        Assert.DoesNotContain("SETUP", stable);
        Assert.Equal(stable, template.Stable("OWNER"));
        Assert.NotEqual(stable, template.Stable("OTHER"));
    }

    [Fact]
    public void ANoOwnerBlockRendersTheRestOfTheTemplate() =>
        Assert.Equal("A\nB\nSETUP\nC", HostTemplate.Parse(Template).Render(string.Empty, "SETUP"));

    [Fact]
    public void AMarkerInsideTheOwnersTextIsNotReplaced()
    {
        var rendered = HostTemplate.Parse(Template).Render("mentions {{setup}} and {{owner}}\n\n", "LIVE");

        Assert.Contains("mentions {{setup}} and {{owner}}", rendered);
        Assert.Equal(1, rendered.Split("LIVE").Length - 1);
    }

    [Theory]
    [InlineData("no markers")]
    [InlineData("{{owner}} only")]
    [InlineData("{{setup}} only")]
    [InlineData("{{owner}} {{owner}} {{setup}}")]
    [InlineData("{{owner}} {{setup}} {{setup}}")]
    public void EachMarkerMustAppearExactlyOnce(string text)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => HostTemplate.Parse(text));

        Assert.Contains("exactly once", exception.Message);
    }

    [Fact]
    public void TheOwnerMarkerMustComeFirst()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => HostTemplate.Parse("{{setup}} then {{owner}}"));

        Assert.Contains("must come before", exception.Message);
    }

    [Fact]
    public void NullTextIsRefused() => Assert.Throws<ArgumentNullException>(() => HostTemplate.Parse(null!));
}
