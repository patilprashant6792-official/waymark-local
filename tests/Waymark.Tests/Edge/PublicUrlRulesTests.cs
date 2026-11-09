using Waymark.Host.Edge;

namespace Waymark.Tests.Edge;

public sealed class PublicUrlRulesTests
{
    [Theory]
    [InlineData("https://my-name.ngrok-free.app", "https://my-name.ngrok-free.app")]
    [InlineData("my-name.ngrok-free.app", "https://my-name.ngrok-free.app")]
    [InlineData("  https://my-name.ngrok-free.app  ", "https://my-name.ngrok-free.app")]
    [InlineData("https://my-name.ngrok-free.app/", "https://my-name.ngrok-free.app")]
    [InlineData("https://My-Name.NGROK-free.app", "https://my-name.ngrok-free.app")]
    [InlineData("https://my-name.ngrok-free.app:443", "https://my-name.ngrok-free.app")]
    [InlineData("https://tunnel.example.com", "https://tunnel.example.com")]
    public void AcceptsAnHttpsOriginOnARealDomain(string input, string expected)
    {
        var ok = PublicUrlRules.TryNormalize(input, out var origin, out var error);

        Assert.True(ok);
        Assert.Equal(expected, origin);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RefusesEmptyInput(string? input)
    {
        var ok = PublicUrlRules.TryNormalize(input, out var origin, out var error);

        Assert.False(ok);
        Assert.Equal(string.Empty, origin);
        Assert.Contains("Enter the address", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesPlainHttp()
    {
        var ok = PublicUrlRules.TryNormalize("http://my-name.ngrok-free.app", out var origin, out var error);

        Assert.False(ok);
        Assert.Equal(string.Empty, origin);
        Assert.Contains("https://", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesCredentialsInTheAddress()
    {
        var ok = PublicUrlRules.TryNormalize("https://user:pass@my-name.ngrok-free.app", out _, out var error);

        Assert.False(ok);
        Assert.Contains("user name", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesANonStandardPort()
    {
        var ok = PublicUrlRules.TryNormalize("https://my-name.ngrok-free.app:8443", out _, out var error);

        Assert.False(ok);
        Assert.Contains("port", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://my-name.ngrok-free.app/mcp")]
    [InlineData("https://my-name.ngrok-free.app/a/b")]
    [InlineData("https://my-name.ngrok-free.app?x=1")]
    [InlineData("https://my-name.ngrok-free.app/#frag")]
    public void RefusesAPathQueryOrFragment(string input)
    {
        var ok = PublicUrlRules.TryNormalize(input, out _, out var error);

        Assert.False(ok);
        Assert.Contains("path", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://localhost")]
    [InlineData("https://LOCALHOST")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://192.168.1.10")]
    [InlineData("https://intranet")]
    [InlineData("https://[::1]")]
    public void RefusesLocalhostIpAddressesAndSingleLabelNames(string input)
    {
        var ok = PublicUrlRules.TryNormalize(input, out var origin, out var error);

        Assert.False(ok);
        Assert.Equal(string.Empty, origin);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void RefusesSomethingThatIsNotAnAddress()
    {
        var ok = PublicUrlRules.TryNormalize("not an address at all", out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }
}
