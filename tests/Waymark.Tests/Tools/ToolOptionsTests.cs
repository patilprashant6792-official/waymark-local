using Waymark.Core.Tools;
using Waymark.Tests.Support;

namespace Waymark.Tests.Tools;

public sealed class ToolOptionsTests
{
    [Fact]
    public void PipelineDefaultsApplyWhenNothingIsConfigured()
    {
        var options = ToolPipelineOptions.FromConfiguration(new TestConfiguration());

        Assert.Equal(TimeSpan.FromSeconds(ToolPipelineOptions.DefaultCallTimeoutSeconds), options.CallTimeout);
        Assert.Equal(ToolPipelineOptions.DefaultMaxOutputChars, options.MaxOutputChars);
    }

    [Fact]
    public void PipelineValuesAreReadFromConfiguration()
    {
        var options = ToolPipelineOptions.FromConfiguration(new TestConfiguration(
            ("Waymark:Tools:CallTimeoutSeconds", "120"),
            ("Waymark:Tools:MaxOutputChars", "5000")));

        Assert.Equal(TimeSpan.FromSeconds(120), options.CallTimeout);
        Assert.Equal(5000, options.MaxOutputChars);
    }

    [Fact]
    public void BlankPipelineValuesFallBackToTheDefaults()
    {
        var options = ToolPipelineOptions.FromConfiguration(new TestConfiguration(
            ("Waymark:Tools:CallTimeoutSeconds", "  "),
            ("Waymark:Tools:MaxOutputChars", string.Empty)));

        Assert.Equal(TimeSpan.FromSeconds(ToolPipelineOptions.DefaultCallTimeoutSeconds), options.CallTimeout);
        Assert.Equal(ToolPipelineOptions.DefaultMaxOutputChars, options.MaxOutputChars);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("+5")]
    public void InvalidTimeoutStopsStartUpNamingTheSetting(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => ToolPipelineOptions.FromConfiguration(new TestConfiguration(("Waymark:Tools:CallTimeoutSeconds", value))));

        Assert.Contains("Waymark:Tools:CallTimeoutSeconds", exception.Message);
    }

    [Fact]
    public void InvalidMaximumOutputStopsStartUpNamingTheSetting()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => ToolPipelineOptions.FromConfiguration(new TestConfiguration(("Waymark:Tools:MaxOutputChars", "zero"))));

        Assert.Contains("Waymark:Tools:MaxOutputChars", exception.Message);
    }

    [Fact]
    public void SafetyDefaultsHideSecretsAndSkipTheFooter()
    {
        var options = SafetyOptions.FromConfiguration(new TestConfiguration());

        Assert.True(options.RedactSecrets);
        Assert.False(options.AppendFooter);
        Assert.Equal(SafetyOptions.DefaultFooterText, options.FooterText);
    }

    [Fact]
    public void SafetyValuesAreReadFromConfiguration()
    {
        var options = SafetyOptions.FromConfiguration(new TestConfiguration(
            ("Waymark:Safety:RedactSecrets", "false"),
            ("Waymark:Safety:AppendFooter", "TRUE"),
            ("Waymark:Safety:FooterText", "  Rotate it.  ")));

        Assert.False(options.RedactSecrets);
        Assert.True(options.AppendFooter);
        Assert.Equal("Rotate it.", options.FooterText);
    }

    [Fact]
    public void BlankFooterTextFallsBackToTheDefault()
    {
        var options = SafetyOptions.FromConfiguration(new TestConfiguration(("Waymark:Safety:FooterText", "   ")));

        Assert.Equal(SafetyOptions.DefaultFooterText, options.FooterText);
    }

    [Fact]
    public void FooterTextLimitIsInclusive()
    {
        var longest = new string('x', SafetyOptions.MaxFooterChars);
        var tooLong = new string('x', SafetyOptions.MaxFooterChars + 1);

        Assert.Equal(longest, SafetyOptions.FromConfiguration(new TestConfiguration(("Waymark:Safety:FooterText", longest))).FooterText);
        Assert.Throws<InvalidOperationException>(
            () => SafetyOptions.FromConfiguration(new TestConfiguration(("Waymark:Safety:FooterText", tooLong))));
    }

    [Fact]
    public void TheBuiltInFooterFitsItsOwnLimit() =>
        Assert.True(SafetyOptions.DefaultFooterText.Length <= SafetyOptions.MaxFooterChars);

    [Theory]
    [InlineData("Waymark:Safety:RedactSecrets")]
    [InlineData("Waymark:Safety:AppendFooter")]
    public void NonBooleanSafetySettingStopsStartUpNamingTheSetting(string key)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => SafetyOptions.FromConfiguration(new TestConfiguration((key, "yes"))));

        Assert.Contains(key, exception.Message);
    }
}
