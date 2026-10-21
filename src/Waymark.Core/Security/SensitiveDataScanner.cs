using System.Globalization;
using System.Text.RegularExpressions;

namespace Waymark.Core.Security;

/// <summary>One kind of secret found in a text and how many times.</summary>
public sealed record RedactionFinding(string Kind, int Count);

/// <summary>The text with every found secret replaced by a marker, and what was found.</summary>
public sealed record RedactionResult(string Text, IReadOnlyList<RedactionFinding> Findings)
{
    public bool HasFindings => Findings.Count > 0;
}

/// <summary>
/// Finds secrets that have a recognisable shape (private keys, cloud and source-host tokens, JWTs, passwords in connection strings and URLs,
/// secret-named JSON values and environment lines) and replaces each with a marker made of <c>[redacted:</c>, the kind and a closing bracket. It only replaces what it is sure enough about:
/// it is a safety net for output that is about to leave this computer, not a promise that nothing sensitive is left.
/// </summary>
public static partial class SensitiveDataScanner
{
    /// <summary>Every marker starts with this. Output that carries a finished marker was produced by Waymark, so a write that contains one is refused.</summary>
    public const string MarkerPrefix = "[redacted:";

    /// <summary>Reported when a pattern ran out of time, so the text could not be fully checked.</summary>
    public const string IncompleteKind = "scan-incomplete";

    private const int TimeoutMilliseconds = 200;

    private sealed record Rule(string Kind, Regex Pattern, Func<Match, string> Replace);

    private static readonly Rule[] Rules =
    [
        Whole("private-key", PrivateKeyBlock()),
        Whole("private-key", PrivateKeyOpen()),
        Whole("aws-access-key", AwsAccessKey()),
        Whole("github-token", GitHubToken()),
        Whole("slack-token", SlackToken()),
        Whole("nuget-api-key", NuGetApiKey()),
        Whole("google-api-key", GoogleApiKey()),
        Whole("api-key", SecretKeyPrefix()),
        Whole("jwt", JsonWebToken()),
        Whole("url-credentials", UrlCredentials()),
        Keep("connection-secret", ConnectionSecret()),
        Keep("json-secret", JsonSecret()),
        Keep("env-secret", EnvSecret()),
    ];

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex PrivateKeyBlock();

    // A key whose end was cut off, for example by an output limit.
    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----[A-Za-z0-9+/=\r\n ]{64,}", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex PrivateKeyOpen();

    [GeneratedRegex(@"\b(?:AKIA|ASIA|AGPA|AIDA|AROA)[0-9A-Z]{16}\b", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9_]{50,255})\b", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"\boy2[a-z0-9]{43}\b", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex NuGetApiKey();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_\-]{35}\b", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex GoogleApiKey();

    [GeneratedRegex(@"\bsk-(?:ant-|proj-)?[A-Za-z0-9_\-]{20,}", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex SecretKeyPrefix();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex JsonWebToken();

    // user:password in front of the @ of a URL. A name without a colon (ssh://git@host) is not a secret.
    [GeneratedRegex(@"(?<=://)[^\s/@:'""<>]+:[^\s/@'""<>]+(?=@)", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex UrlCredentials();

    // Password=...; in a connection string or --password=... on a command line. No spaces around the = so that code such as 'user.Password = hash;' is left alone.
    [GeneratedRegex(@"(?<keep>\b[A-Za-z_]{0,40}(?:password|pwd|accountkey|sharedaccesskey)=)(?!\[redacted:)[^;""'\r\n]{3,}", RegexOptions.IgnoreCase, TimeoutMilliseconds)]
    private static partial Regex ConnectionSecret();

    // "somePassword": "value" - the name must end with the sensitive word, so "tokenType" and "max_tokens" are not touched.
    [GeneratedRegex(@"(?<keep>""[A-Za-z0-9_.\-]*(?:password|passwd|pwd|secret|apikey|api_key|accesskey|access_key|privatekey|private_key|token)""\s*:\s*"")(?!\[redacted:)[^""\r\n]{4,}(?="")", RegexOptions.IgnoreCase, TimeoutMilliseconds)]
    private static partial Regex JsonSecret();

    // GITHUB_TOKEN=... lines as printed by 'set', 'env' or a .env file. The name must end with the sensitive word, so MAX_TOKEN_COUNT is not touched.
    [GeneratedRegex(@"(?<keep>^[ \t]*(?:export[ \t]+)?[A-Z][A-Z0-9_]*(?:PASSWORD|SECRET|TOKEN|API_?KEY|ACCESS_?KEY|PRIVATE_?KEY|CONNECTION_STRING)[ \t]*=[ \t]*)(?!\[redacted:)[^\r\n]{4,}", RegexOptions.Multiline, TimeoutMilliseconds)]
    private static partial Regex EnvSecret();

    public static string Marker(string kind) => MarkerPrefix + kind + "]";

    /// <summary>
    /// True when the text contains a finished marker (the prefix, a kind and the closing bracket). The bare prefix, as written in this very source file,
    /// does not count, so Waymark can still be used to edit its own code. A pattern that runs out of time counts as a marker: the write is refused.
    /// </summary>
    public static bool ContainsMarker(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            return FinishedMarker().IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    [GeneratedRegex(@"\[redacted:[a-z][a-z\-]*\]", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex FinishedMarker();

    public static RedactionResult Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return new RedactionResult(text, []);
        }

        var findings = new List<RedactionFinding>();
        var current = text;
        foreach (var rule in Rules)
        {
            var count = 0;
            try
            {
                current = rule.Pattern.Replace(current, match =>
                {
                    count++;
                    return rule.Replace(match);
                });
            }
            catch (RegexMatchTimeoutException)
            {
                Add(findings, IncompleteKind, 1);
                continue;
            }

            if (count > 0)
            {
                Add(findings, rule.Kind, count);
            }
        }

        return new RedactionResult(current, findings);
    }

    /// <summary>Adds a finding to a list, merging it with an earlier finding of the same kind.</summary>
    public static void Add(List<RedactionFinding> findings, string kind, int count)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var index = findings.FindIndex(f => string.Equals(f.Kind, kind, StringComparison.Ordinal));
        if (index < 0)
        {
            findings.Add(new RedactionFinding(kind, count));
        }
        else
        {
            findings[index] = findings[index] with { Count = findings[index].Count + count };
        }
    }

    /// <summary>For example "github-token, json-secret x3". Contains kinds and counts only, never a value.</summary>
    public static string Summarize(IEnumerable<RedactionFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return string.Join(
            ", ",
            findings.Select(f => f.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $"{f.Kind} x{f.Count}") : f.Kind));
    }

    private static Rule Whole(string kind, Regex pattern) => new(kind, pattern, _ => Marker(kind));

    private static Rule Keep(string kind, Regex pattern) => new(kind, pattern, match => match.Groups["keep"].Value + Marker(kind));
}
