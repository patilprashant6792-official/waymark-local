using Waymark.Core.Auth;
using Waymark.Host.Auth;

namespace Waymark.Tests.Auth;

public sealed class OAuthRulesTests
{
    // RFC 7636 appendix B test vector.
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback")]
    [InlineData("http://localhost/callback")]
    [InlineData("http://localhost:8123/callback")]
    [InlineData("http://127.0.0.1:54321/callback")]
    public void RegistrationAllowsTheHostedCallbackAndLoopbackCallbacks(string uri) =>
        Assert.True(RedirectUriPolicy.IsAllowedForRegistration(uri));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://claude.ai/api/mcp/auth_callback/")]
    [InlineData("https://claude.ai/other")]
    [InlineData("https://localhost/callback")]
    [InlineData("http://localhost/other")]
    [InlineData("http://localhost:8123/callback?x=1")]
    [InlineData("http://localhost:8123/callback#frag")]
    [InlineData("http://user@localhost:8123/callback")]
    [InlineData("http://evil.example/callback")]
    [InlineData("http://localhost.evil.example/callback")]
    [InlineData("http://[::1]/callback")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a uri")]
    public void RegistrationRefusesEverythingElse(string? uri) =>
        Assert.False(RedirectUriPolicy.IsAllowedForRegistration(uri));

    [Fact]
    public void AnExactMatchIsAccepted() =>
        Assert.True(RedirectUriPolicy.Matches([RedirectUriPolicy.HostedCallback], RedirectUriPolicy.HostedCallback));

    [Fact]
    public void ALoopbackCallbackMatchesOnAnyPortOfTheSameHost()
    {
        var registered = new[] { "http://localhost:8000/callback" };

        Assert.True(RedirectUriPolicy.Matches(registered, "http://localhost:51234/callback"));
        Assert.True(RedirectUriPolicy.Matches(registered, "http://localhost/callback"));
    }

    [Fact]
    public void ALoopbackCallbackOnAnotherHostDoesNotMatch()
    {
        var registered = new[] { "http://localhost:8000/callback" };

        Assert.False(RedirectUriPolicy.Matches(registered, "http://127.0.0.1:8000/callback"));
    }

    [Fact]
    public void ALoopbackRequestDoesNotMatchAClientRegisteredOnlyWithTheHostedCallback() =>
        Assert.False(RedirectUriPolicy.Matches([RedirectUriPolicy.HostedCallback], "http://localhost:8000/callback"));

    [Fact]
    public void AnArbitraryUriNeverMatchesByPortFlexibility()
    {
        var registered = new[] { "http://localhost:8000/callback" };

        Assert.False(RedirectUriPolicy.Matches(registered, "http://localhost:9000/other"));
        Assert.False(RedirectUriPolicy.Matches(registered, "http://evil.example:8000/callback"));
        Assert.False(RedirectUriPolicy.Matches(registered, "https://localhost:8000/callback"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AMissingRequestedUriNeverMatches(string? requested) =>
        Assert.False(RedirectUriPolicy.Matches([RedirectUriPolicy.HostedCallback], requested));

    [Fact]
    public void ANullRegisteredListIsRefused() =>
        Assert.Throws<ArgumentNullException>(() => RedirectUriPolicy.Matches(null!, "http://localhost/callback"));

    [Fact]
    public void PkceAcceptsTheRfcTestVector()
    {
        Assert.True(Pkce.Verify(Verifier, Challenge));
        Assert.True(Pkce.IsValidChallenge(Challenge));
    }

    [Fact]
    public void PkceRejectsAWrongVerifier() =>
        Assert.False(Pkce.Verify(new string('a', 43), Challenge));

    [Fact]
    public void PkceRejectsAVerifierThatDiffersByOneCharacter() =>
        Assert.False(Pkce.Verify(Verifier[..^1] + "l", Challenge));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void PkceRejectsMissingOrShortVerifiers(string? verifier) =>
        Assert.False(Pkce.Verify(verifier, Challenge));

    [Fact]
    public void PkceRejectsAMalformedChallenge()
    {
        Assert.False(Pkce.Verify(Verifier, "short"));
        Assert.False(Pkce.Verify(Verifier, Challenge + "+"));
    }

    [Theory]
    [InlineData(42, false)]
    [InlineData(43, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void ChallengeAndVerifierLengthsAreBoundedTo43Through128(int length, bool expected)
    {
        Assert.Equal(expected, Pkce.IsValidChallenge(new string('A', length)));
        Assert.False(Pkce.Verify(new string('A', length), new string('B', 43)));
    }

    [Theory]
    [InlineData("+")]
    [InlineData("/")]
    [InlineData("=")]
    [InlineData(" ")]
    public void ChallengesOutsideTheUnreservedAlphabetAreRefused(string badCharacter) =>
        Assert.False(Pkce.IsValidChallenge(new string('A', 42) + badCharacter));

    [Fact]
    public void ChallengesMayUseTheAllowedPunctuation() =>
        Assert.True(Pkce.IsValidChallenge("A-._~" + new string('b', 38)));

    [Fact]
    public void NullIsNotAValidChallenge() =>
        Assert.False(Pkce.IsValidChallenge(null));

    [Theory]
    [InlineData(null, "mcp")]
    [InlineData("", "mcp")]
    [InlineData("   ", "mcp")]
    [InlineData("mcp", "mcp")]
    [InlineData("offline_access", "mcp offline_access")]
    [InlineData("offline_access mcp", "mcp offline_access")]
    [InlineData("mcp offline_access", "mcp offline_access")]
    [InlineData("  mcp   offline_access  ", "mcp offline_access")]
    [InlineData("mcp mcp", "mcp")]
    public void NormalizesScopesAndAlwaysGrantsMcp(string? requested, string expected)
    {
        var ok = OAuthScopes.TryNormalize(requested, out var normalized);

        Assert.True(ok);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("mcp admin")]
    [InlineData("MCP")]
    [InlineData("offline_access write")]
    public void UnknownScopesAreRefused(string requested)
    {
        var ok = OAuthScopes.TryNormalize(requested, out var normalized);

        Assert.False(ok);
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void TheNormalizedScopeUsesTheDefinedConstants()
    {
        OAuthScopes.TryNormalize(AuthDefaults.OfflineAccessScope, out var normalized);

        Assert.Equal(AuthDefaults.McpScope + " " + AuthDefaults.OfflineAccessScope, normalized);
    }

    [Theory]
    [InlineData("mcp offline_access", "offline_access", true)]
    [InlineData("mcp offline_access", "mcp", true)]
    [InlineData("mcp", "offline_access", false)]
    [InlineData("mcp2", "mcp", false)]
    [InlineData("", "mcp", false)]
    public void IncludesChecksWholeScopeWords(string scope, string wanted, bool expected) =>
        Assert.Equal(expected, OAuthScopes.Includes(scope, wanted));
}
