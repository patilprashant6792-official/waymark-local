using Waymark.Host.Edge;
using Waymark.Tests.Support;

namespace Waymark.Tests.Edge;

public sealed class EdgeOptionsTests
{
    [Fact]
    public void DefaultsApplyWhenNothingIsConfigured()
    {
        var options = EdgeOptions.FromConfiguration(new TestConfiguration());

        Assert.Equal(47100, options.AdminPort);
        Assert.Equal(47101, options.McpPort);
        Assert.Equal(EdgeOptions.DefaultAdminPort, options.AdminPort);
        Assert.Equal(EdgeOptions.DefaultMcpPort, options.McpPort);
    }

    [Fact]
    public void ConfiguredPortsAreUsed()
    {
        var options = EdgeOptions.FromConfiguration(new TestConfiguration(("Waymark:AdminPort", "5000"), ("Waymark:McpPort", "5001")));

        Assert.Equal(5000, options.AdminPort);
        Assert.Equal(5001, options.McpPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void BlankValuesFallBackToTheDefault(string blank)
    {
        var options = EdgeOptions.FromConfiguration(new TestConfiguration(("Waymark:AdminPort", blank)));

        Assert.Equal(EdgeOptions.DefaultAdminPort, options.AdminPort);
    }

    [Theory]
    [InlineData("1023")]
    [InlineData("65536")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("80.5")]
    [InlineData("+5000")]
    public void APortOutsideTheAllowedRangeOrNotANumberIsRefused(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => EdgeOptions.FromConfiguration(new TestConfiguration(("Waymark:McpPort", value))));

        Assert.Contains("Waymark:McpPort", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1024")]
    [InlineData("65535")]
    public void TheBoundsAreAllowed(string value)
    {
        var options = EdgeOptions.FromConfiguration(new TestConfiguration(("Waymark:AdminPort", value)));

        Assert.Equal(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture), options.AdminPort);
    }

    [Fact]
    public void TheTwoPortsMustDiffer() =>
        Assert.Throws<InvalidOperationException>(
            () => EdgeOptions.FromConfiguration(new TestConfiguration(("Waymark:AdminPort", "5000"), ("Waymark:McpPort", "5000"))));

    [Fact]
    public void ConfiguringOnlyOnePortToTheOthersDefaultIsRefused() =>
        Assert.Throws<InvalidOperationException>(
            () => EdgeOptions.FromConfiguration(new TestConfiguration(("Waymark:AdminPort", "47101"))));

    [Fact]
    public void ANullConfigurationIsRefused() =>
        Assert.Throws<ArgumentNullException>(() => EdgeOptions.FromConfiguration(null!));
}
