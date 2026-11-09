using Waymark.Core.Security;

namespace Waymark.Tests.Security;

public sealed class SensitiveDataScannerTests
{
    // Secret-shaped values are built at run time, so no token-like literal sits in the repository.
    private static string Repeat(char character, int count) => new(character, count);

    // Single quotes stand in for double quotes, which keeps the JSON samples readable.
    private static string Json(string text) => text.Replace('\'', '"');

    public static TheoryData<string, string> WholeSecrets => new()
    {
        { "aws-access-key", "AKIA" + "IOSFODNN7EXAMPLE" },
        { "github-token", "ghp_" + Repeat('A', 36) },
        { "github-token", "github_pat_" + Repeat('b', 60) },
        { "slack-token", "xoxb-" + "1234567890-abcdefghij" },
        { "nuget-api-key", "oy2" + Repeat('a', 43) },
        { "google-api-key", "AIza" + Repeat('x', 35) },
        { "api-key", "sk-" + Repeat('a', 24) },
        { "api-key", "sk-ant-" + Repeat('b', 30) },
        { "jwt", "eyJ" + Repeat('a', 12) + ".eyJ" + Repeat('b', 12) + "." + Repeat('c', 12) },
    };

    public static TheoryData<string> PrivateKeyLabels => new()
    {
        "RSA PRIVATE KEY",
        "PRIVATE KEY",
        "EC PRIVATE KEY",
        "OPENSSH PRIVATE KEY",
        "ENCRYPTED PRIVATE KEY",
        "PGP PRIVATE KEY BLOCK",
    };

    [Fact]
    public void EmptyTextHasNothingToHide()
    {
        var result = SensitiveDataScanner.Redact(string.Empty);

        Assert.Equal(string.Empty, result.Text);
        Assert.False(result.HasFindings);
    }

    [Fact]
    public void NullTextIsRefused() => Assert.Throws<ArgumentNullException>(() => SensitiveDataScanner.Redact(null!));

    [Fact]
    public void OrdinaryCodeIsReturnedUnchanged()
    {
        var text = "var max_tokens = 4096; // tokenType and password policy\nuser.Password = hash;";

        var result = SensitiveDataScanner.Redact(text);

        Assert.Equal(text, result.Text);
        Assert.False(result.HasFindings);
        Assert.Empty(result.Findings);
    }

    [Theory]
    [MemberData(nameof(WholeSecrets))]
    public void SecretWithARecognisableShapeIsReplacedByItsMarker(string kind, string secret)
    {
        var result = SensitiveDataScanner.Redact($"value: {secret} end");

        Assert.Equal($"value: {SensitiveDataScanner.Marker(kind)} end", result.Text);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(kind, finding.Kind);
        Assert.Equal(1, finding.Count);
        Assert.DoesNotContain(secret, result.Text);
        Assert.True(result.HasFindings);
    }

    [Fact]
    public void EveryOccurrenceIsReplacedAndCounted()
    {
        var token = "ghp_" + Repeat('A', 36);

        var result = SensitiveDataScanner.Redact($"{token} and again {token}");

        var marker = SensitiveDataScanner.Marker("github-token");
        Assert.Equal($"{marker} and again {marker}", result.Text);
        Assert.Equal(2, Assert.Single(result.Findings).Count);
    }

    [Theory]
    [MemberData(nameof(PrivateKeyLabels))]
    public void PrivateKeyBlockIsReplacedWhole(string label)
    {
        var key = $"-----BEGIN {label}-----\nMIIBVgIBADANBgkqhkiG9w0B\nAQEFAASCAUAwggE8\n-----END {label}-----";

        var result = SensitiveDataScanner.Redact($"before\n{key}\nafter");

        Assert.Equal($"before\n{SensitiveDataScanner.Marker("private-key")}\nafter", result.Text);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("private-key", finding.Kind);
        Assert.Equal(1, finding.Count);
    }

    [Fact]
    public void PrivateKeyThatWasCutOffByALimitIsReplacedToo()
    {
        var cut = "-----BEGIN PRIVATE KEY-----\n" + Repeat('A', 80);

        var result = SensitiveDataScanner.Redact(cut);

        Assert.Equal(SensitiveDataScanner.Marker("private-key"), result.Text);
    }

    [Fact]
    public void PublicKeyIsNotASecret()
    {
        var text = "-----BEGIN PUBLIC KEY-----\nMIIBIjANBgkqhkiG9w0B\n-----END PUBLIC KEY-----";

        var result = SensitiveDataScanner.Redact(text);

        Assert.Equal(text, result.Text);
        Assert.False(result.HasFindings);
    }

    [Fact]
    public void PasswordInAUrlIsHiddenButAUserNameAloneIsNot()
    {
        var result = SensitiveDataScanner.Redact("clone https://admin:s3cr3tPass@example.com/repo.git and ssh://git@github.com/x/y.git");

        var expected = $"clone https://{SensitiveDataScanner.Marker("url-credentials")}@example.com/repo.git and ssh://git@github.com/x/y.git";
        Assert.Equal(expected, result.Text);
        Assert.Equal("url-credentials", Assert.Single(result.Findings).Kind);
    }

    [Theory]
    [InlineData("http://localhost:8080/path")]
    [InlineData("mailto:user@example.com")]
    [InlineData("ssh://git@host/x.git")]
    [InlineData("http://host:80/x@y")]
    public void UrlsWithoutAPasswordAreLeftAlone(string text)
    {
        var result = SensitiveDataScanner.Redact(text);

        Assert.Equal(text, result.Text);
        Assert.False(result.HasFindings);
    }

    [Theory]
    [InlineData("Server=.;Database=x;Password=hunter2real;Trusted=true", "Server=.;Database=x;Password=", ";Trusted=true")]
    [InlineData("Uid=sa;Pwd=abc123", "Uid=sa;Pwd=", "")]
    [InlineData("AccountKey=abc123def456==;EndpointSuffix=core.windows.net", "AccountKey=", ";EndpointSuffix=core.windows.net")]
    [InlineData("SharedAccessKey=Zm9vYmFy;Foo=1", "SharedAccessKey=", ";Foo=1")]
    [InlineData("run --password=hunter22", "run --password=", "")]
    public void ValueAfterAPasswordLikeNameIsHiddenAndTheNameIsKept(string text, string keep, string rest)
    {
        var result = SensitiveDataScanner.Redact(text);

        Assert.Equal(keep + SensitiveDataScanner.Marker("connection-secret") + rest, result.Text);
        Assert.Equal("connection-secret", Assert.Single(result.Findings).Kind);
    }

    [Theory]
    [InlineData("user.Password = hash;")]
    [InlineData("Password=ab")]
    [InlineData("var passwordLength = 12;")]
    [InlineData("max_tokens: 4096")]
    public void ThingsThatOnlyLookLikePasswordsAreLeftAlone(string text)
    {
        var result = SensitiveDataScanner.Redact(text);

        Assert.Equal(text, result.Text);
        Assert.False(result.HasFindings);
    }

    [Fact]
    public void PasswordEnvironmentLineIsHandledByTheConnectionRuleFirst()
    {
        var result = SensitiveDataScanner.Redact("DB_PASSWORD=hunter22");

        Assert.Equal("DB_PASSWORD=" + SensitiveDataScanner.Marker("connection-secret"), result.Text);
        Assert.Equal("connection-secret", Assert.Single(result.Findings).Kind);
    }

    [Fact]
    public void JsonValueUnderASecretNamedKeyIsHidden()
    {
        var json = Json("{'password': 'hunter2real', 'apiKey':'abcd1234', 'clientSecret' : 'xyz12345'}");

        var result = SensitiveDataScanner.Redact(json);

        var marker = SensitiveDataScanner.Marker("json-secret");
        var expected = Json("{'password': '" + marker + "', 'apiKey':'" + marker + "', 'clientSecret' : '" + marker + "'}");
        Assert.Equal(expected, result.Text);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("json-secret", finding.Kind);
        Assert.Equal(3, finding.Count);
    }

    [Fact]
    public void JsonKeysThatOnlyContainASecretWordAreLeftAlone()
    {
        var json = Json("{'max_tokens': 4096, 'tokenType': 'Bearer', 'tokens': 'abcdefgh', 'password': 'abc'}");

        var result = SensitiveDataScanner.Redact(json);

        Assert.Equal(json, result.Text);
        Assert.False(result.HasFindings);
    }

    [Fact]
    public void SecretNamedEnvironmentLinesKeepTheirNameAndLoseTheValue()
    {
        var marker = SensitiveDataScanner.Marker("env-secret");
        var text = "NAME=app\nGITHUB_TOKEN=abcd1234efgh\nexport DB_SECRET=zzzz9999\n  STRIPE_API_KEY=value1234\nMAX_TOKEN_COUNT=5000\nlower_token=abcdefgh";

        var result = SensitiveDataScanner.Redact(text);

        var expected = $"NAME=app\nGITHUB_TOKEN={marker}\nexport DB_SECRET={marker}\n  STRIPE_API_KEY={marker}\nMAX_TOKEN_COUNT=5000\nlower_token=abcdefgh";
        Assert.Equal(expected, result.Text);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("env-secret", finding.Kind);
        Assert.Equal(3, finding.Count);
    }

    [Fact(Skip = "Known gap: the env-secret rule needs at least one letter before the sensitive word, so bare PASSWORD=..., TOKEN=..., SECRET=... and API_KEY=... lines are not hidden by it. Enable once the rule accepts a bare name.")]
    public void BareSecretNamedEnvironmentLinesAreHiddenToo()
    {
        var result = SensitiveDataScanner.Redact("PASSWORD=hunter2hunter2\nTOKEN=abcd1234efgh\nAPI_KEY=value1234");

        Assert.DoesNotContain("hunter2hunter2", result.Text);
        Assert.DoesNotContain("abcd1234efgh", result.Text);
        Assert.DoesNotContain("value1234", result.Text);
    }

    [Fact]
    public void EnvironmentLinesWithWindowsLineEndingsStopAtTheLineBreak()
    {
        var marker = SensitiveDataScanner.Marker("env-secret");

        var result = SensitiveDataScanner.Redact("GITHUB_TOKEN=abcd1234efgh\r\nNAME=app\r\n");

        Assert.Equal($"GITHUB_TOKEN={marker}\r\nNAME=app\r\n", result.Text);
    }

    [Fact]
    public void FindingsFollowTheRuleOrderAndTheSummaryListsThem()
    {
        var token = "ghp_" + Repeat('A', 36);
        var text = $"token {token}\nPassword=hunter2real;\n" + Json("{'secret': 'abcdefgh'}");

        var result = SensitiveDataScanner.Redact(text);

        Assert.Equal(new[] { "github-token", "connection-secret", "json-secret" }, result.Findings.Select(finding => finding.Kind));
        Assert.Equal("github-token, connection-secret, json-secret", SensitiveDataScanner.Summarize(result.Findings));
        Assert.DoesNotContain("hunter2real", result.Text);
        Assert.DoesNotContain("abcdefgh", result.Text);
    }

    [Fact]
    public void RedactingTwiceChangesNothingTheSecondTime()
    {
        var token = "ghp_" + Repeat('A', 36);
        var text = $"{token}\nServer=.;Password=hunter2real\nGITHUB_TOKEN=abcd1234efgh\n" + Json("{'password': 'hunter2real'}");

        var first = SensitiveDataScanner.Redact(text);
        var second = SensitiveDataScanner.Redact(first.Text);

        Assert.True(first.HasFindings);
        Assert.Equal(first.Text, second.Text);
        Assert.False(second.HasFindings);
    }

    [Fact]
    public void LargeCleanTextIsScannedWithoutTimingOut()
    {
        var text = string.Concat(Enumerable.Repeat("var total = items.Sum(x => x.Price); // password policy\n", 2_000));

        var result = SensitiveDataScanner.Redact(text);

        Assert.False(result.HasFindings);
        Assert.DoesNotContain(result.Findings, finding => finding.Kind == SensitiveDataScanner.IncompleteKind);
        Assert.Equal(text, result.Text);
    }

    [Theory(Skip = "Known gap: the connection-secret rule also hides placeholders such as ${DB_PASSWORD}. Enable once the rule skips placeholders.")]
    [InlineData("Password=${DB_PASSWORD}")]
    [InlineData("Password={password}")]
    [InlineData("pwd=%DB_PWD%")]
    [InlineData("--password=<your-password>")]
    public void PlaceholderValuesAreNotTreatedAsSecrets(string text) =>
        Assert.False(SensitiveDataScanner.Redact(text).HasFindings);

    [Fact]
    public void MarkerIsThePrefixThenTheKindThenABracket()
    {
        Assert.Equal("[redacted" + ":", SensitiveDataScanner.MarkerPrefix);
        Assert.Equal(SensitiveDataScanner.MarkerPrefix + "jwt" + "]", SensitiveDataScanner.Marker("jwt"));
        Assert.Equal("scan-incomplete", SensitiveDataScanner.IncompleteKind);
    }

    [Fact]
    public void FinishedMarkerIsRecognisedButTheBarePrefixIsNot()
    {
        Assert.True(SensitiveDataScanner.ContainsMarker("x " + SensitiveDataScanner.Marker("json-secret") + " y"));
        Assert.False(SensitiveDataScanner.ContainsMarker(SensitiveDataScanner.MarkerPrefix + " bare"));
        Assert.False(SensitiveDataScanner.ContainsMarker(SensitiveDataScanner.MarkerPrefix + "Upper" + "]"));
        Assert.False(SensitiveDataScanner.ContainsMarker(string.Empty));
        Assert.Throws<ArgumentNullException>(() => SensitiveDataScanner.ContainsMarker(null!));
    }

    [Fact]
    public void AddMergesFindingsOfTheSameKind()
    {
        var findings = new List<RedactionFinding>();

        SensitiveDataScanner.Add(findings, "a", 1);
        SensitiveDataScanner.Add(findings, "b", 2);
        SensitiveDataScanner.Add(findings, "a", 4);

        Assert.Equal(new[] { new RedactionFinding("a", 5), new RedactionFinding("b", 2) }, findings);
        Assert.Throws<ArgumentNullException>(() => SensitiveDataScanner.Add(null!, "a", 1));
    }

    [Fact]
    public void SummaryShowsACountOnlyWhenThereIsMoreThanOne()
    {
        var summary = SensitiveDataScanner.Summarize([new RedactionFinding("github-token", 1), new RedactionFinding("json-secret", 3)]);

        Assert.Equal("github-token, json-secret x3", summary);
        Assert.Equal(string.Empty, SensitiveDataScanner.Summarize([]));
        Assert.Throws<ArgumentNullException>(() => SensitiveDataScanner.Summarize(null!));
    }
}
