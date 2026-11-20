using Microsoft.Extensions.Configuration;
using Waymark.Host.Feedback;
using Waymark.Host.Ui;

namespace Waymark.Tests.Feedback;

public sealed class FeedbackOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();

    [Fact]
    public void NothingConfiguredGivesTheDefaults() =>
        Assert.Equal(FeedbackOptions.Default, FeedbackOptions.FromConfiguration(Config()));

    [Fact]
    public void ConfiguredValuesAreUsed()
    {
        var options = FeedbackOptions.FromConfiguration(Config(
            ("Feedback:Recipient", " me@example.com "),
            ("Feedback:SubjectPrefix", "[Fork]"),
            ("Feedback:MaxLinkLength", "1200")));

        Assert.Equal(new FeedbackOptions("me@example.com", "[Fork]", 1200), options);
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("Name <me@example.com>")]
    [InlineData("me@example.com?cc=other@example.com")]
    [InlineData("me@example.com,other@example.com")]
    public void AnythingButOneBareAddressIsRefused(string recipient) =>
        Assert.Throws<InvalidOperationException>(() => FeedbackOptions.FromConfiguration(Config(("Feedback:Recipient", recipient))));

    [Theory]
    [InlineData("100")]
    [InlineData("99999")]
    [InlineData("lots")]
    public void ALinkLengthOutOfRangeIsRefused(string value) =>
        Assert.Throws<InvalidOperationException>(() => FeedbackOptions.FromConfiguration(Config(("Feedback:MaxLinkLength", value))));

    [Fact]
    public void AMultiLinePrefixIsRefused() =>
        Assert.Throws<InvalidOperationException>(() => FeedbackOptions.FromConfiguration(Config(("Feedback:SubjectPrefix", "a\nb"))));

    [Theory]
    [InlineData(null, "0.3")]
    [InlineData("1.0.0", "0.3")]
    [InlineData("1.0.0+", "0.3")]
    [InlineData("1.0.0+eb7f7da0123456789", "0.3 (eb7f7da)")]
    [InlineData("1.0.0+abc", "0.3 (abc)")]
    public void VersionCarriesTheShortCommitWhenStamped(string? informational, string expected) =>
        Assert.Equal(expected, AppInfo.Describe(informational));
}
