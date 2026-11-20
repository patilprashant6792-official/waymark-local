using Waymark.Core.Security;
using Waymark.Host.Feedback;

namespace Waymark.Tests.Feedback;

public sealed class FeedbackComposerTests
{
    private static readonly FeedbackSystemInfo System = new("0.3 (test)", "Test OS 1.0", ".NET 10.0.0");

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    // Secret-shaped values are built at run time, so no token-like literal sits in the repository.
    private static string GitHubToken() => "ghp_" + new string('A', 36);

    private static FeedbackComposer Composer(FeedbackOptions? options = null, IOutboundTextScrubber? scrubber = null) =>
        new(scrubber ?? new PassThrough(), options ?? FeedbackOptions.Default, System);

    private static string LinkBody(string mailto) => Uri.UnescapeDataString(mailto[(mailto.IndexOf("&body=", StringComparison.Ordinal) + 6)..]);

    private static string LinkSubject(string mailto)
    {
        var start = mailto.IndexOf("?subject=", StringComparison.Ordinal) + 9;
        var end = mailto.IndexOf("&body=", StringComparison.Ordinal);
        return Uri.UnescapeDataString(mailto[start..end]);
    }

    private Task<FeedbackMail> ComposeAsync(string message, FeedbackKind kind = FeedbackKind.Bug, bool system = true, FeedbackComposer? composer = null) =>
        (composer ?? Composer()).ComposeAsync(new FeedbackDraft(kind, message, system), _ct);

    [Fact]
    public async Task LinkGoesToTheTaggedAddressWithThePlusEncoded()
    {
        var mail = await ComposeAsync("Hello");

        Assert.StartsWith("mailto:patilprashant6792.official%2Bwaymark-feedback@gmail.com?subject=", mail.MailtoUri, StringComparison.Ordinal);
        Assert.Equal(FeedbackOptions.DefaultRecipient, mail.Recipient);
    }

    [Fact]
    public async Task SpacesAndLineBreaksAreEncodedTheWayMailAppsRead()
    {
        var mail = await ComposeAsync("two words\nnext line");

        Assert.DoesNotContain("+", mail.MailtoUri[mail.MailtoUri.IndexOf('?', StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("two%20words%0D%0Anext%20line", mail.MailtoUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpecialCharactersSurviveTheLink()
    {
        const string Message = "a & b ? c # d % e = f + g \u00e9 \u4f60\u597d \U0001F600";

        var mail = await ComposeAsync(Message);

        Assert.Equal(mail.Body, LinkBody(mail.MailtoUri));
        Assert.StartsWith(Message, mail.Body, StringComparison.Ordinal);
        Assert.Equal(mail.Subject, LinkSubject(mail.MailtoUri));
    }

    [Theory]
    [InlineData(FeedbackKind.Bug, "[Waymark Feedback] Bug: It crashed")]
    [InlineData(FeedbackKind.Praise, "[Waymark Feedback] Praise: It crashed")]
    public async Task SubjectStartsWithThePrefixAndKind(FeedbackKind kind, string expected)
    {
        var mail = await ComposeAsync("\n\n  It   crashed  \nmore detail", kind);

        Assert.Equal(expected, mail.Subject);
    }

    [Fact]
    public async Task LongFirstLineIsCutInTheSubject()
    {
        var mail = await ComposeAsync(new string('x', 200));

        Assert.Equal("[Waymark Feedback] Bug: " + new string('x', 60) + "...", mail.Subject);
    }

    [Fact]
    public async Task ATokenIsHiddenBeforeItLeaves()
    {
        var token = GitHubToken();

        var mail = await ComposeAsync("my token is " + token);

        Assert.True(mail.SecretsHidden);
        Assert.DoesNotContain(token, mail.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(token, mail.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain(token, mail.CopyText, StringComparison.Ordinal);
        Assert.DoesNotContain(token, LinkBody(mail.MailtoUri), StringComparison.Ordinal);
        Assert.Contains(SensitiveDataScanner.MarkerPrefix, mail.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnownValuesAreHiddenByTheScrubber()
    {
        var composer = Composer(scrubber: new PassThrough("hunter2"));

        var mail = await ComposeAsync("the vault value is hunter2", composer: composer);

        Assert.True(mail.SecretsHidden);
        Assert.DoesNotContain("hunter2", mail.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainTextIsNotMarkedAsHidden()
    {
        var mail = await ComposeAsync("All good, thanks");

        Assert.False(mail.SecretsHidden);
        Assert.False(mail.Shortened);
    }

    [Fact]
    public async Task SystemInfoIsOnlySentWhenAskedFor()
    {
        var with = await ComposeAsync("Hi", system: true);
        var without = await ComposeAsync("Hi", system: false);

        Assert.Contains("Waymark: 0.3 (test)", with.Body, StringComparison.Ordinal);
        Assert.Contains("OS: Test OS 1.0", with.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Waymark: ", without.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("OS: ", without.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(".NET: ", without.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALongMessageIsShortenedInTheLinkOnly()
    {
        var options = FeedbackOptions.Default with { MaxLinkLength = 1000 };
        var message = string.Join(' ', Enumerable.Repeat("word", 700));

        var mail = await ComposeAsync(message, composer: Composer(options));

        Assert.True(mail.Shortened);
        Assert.True(mail.MailtoUri.Length <= 1000, "The link is too long: " + mail.MailtoUri.Length);
        Assert.Contains(message, mail.Body, StringComparison.Ordinal);
        Assert.Contains(message, mail.CopyText, StringComparison.Ordinal);
        Assert.Contains("[The message was shortened to fit the email link.]", LinkBody(mail.MailtoUri), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShorteningNeverSplitsAnEmoji()
    {
        var options = FeedbackOptions.Default with { MaxLinkLength = 1000 };
        var message = string.Concat(Enumerable.Repeat("\U0001F600", 300));

        // A lone surrogate would make Uri.EscapeDataString throw; the composer must cut between pairs.
        var mail = await ComposeAsync(message, composer: Composer(options));

        Assert.True(mail.Shortened);
        Assert.True(mail.MailtoUri.Length <= 1000, "The link is too long: " + mail.MailtoUri.Length);
        Assert.StartsWith("\U0001F600", LinkBody(mail.MailtoUri), StringComparison.Ordinal);
        Assert.EndsWith("\U0001F600...", mail.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWideSubjectStaysSmallInTheLink()
    {
        var mail = await ComposeAsync(string.Concat(Enumerable.Repeat("\u4f60", 200)));

        Assert.True(Uri.EscapeDataString(mail.Subject).Length < 200, mail.Subject);
        Assert.Equal(mail.Subject, LinkSubject(mail.MailtoUri));
    }

    [Fact]
    public async Task CopyTextCarriesAddressSubjectAndBody()
    {
        var mail = await ComposeAsync("Hi");

        Assert.Equal("To: " + mail.Recipient + "\r\nSubject: " + mail.Subject + "\r\n\r\n" + mail.Body, mail.CopyText);
    }

    [Fact]
    public async Task ControlCharactersAreDropped()
    {
        var mail = await ComposeAsync("a\u0000b\u0007c\td");

        Assert.StartsWith("abc\td", mail.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public async Task AnEmptyMessageIsRefused(string message) =>
        await Assert.ThrowsAsync<ArgumentException>(() => ComposeAsync(message));

    [Fact]
    public async Task AMessageOverTheLimitIsRefused() =>
        await Assert.ThrowsAsync<ArgumentException>(() => ComposeAsync(new string('a', FeedbackOptions.MaxMessageLength + 1)));

    private sealed class PassThrough(string? hide = null) : IOutboundTextScrubber
    {
        public Task<string> HideKnownSecretsAsync(string text, CancellationToken ct) =>
            Task.FromResult(hide is null ? text : text.Replace(hide, SensitiveDataScanner.Marker("vault-secret"), StringComparison.Ordinal));
    }
}
