using System.Globalization;
using System.Net.Mail;

namespace Waymark.Host.Feedback;

/// <summary>
/// Where feedback from the admin UI goes and how long the mail link may get. Read once at start-up from the Feedback section of the
/// configuration; every value has a default, so nobody has to set anything. A wrong value stops the start-up instead of failing later.
/// </summary>
public sealed record FeedbackOptions(string Recipient, string SubjectPrefix, int MaxLinkLength)
{
    /// <summary>The +waymark-feedback tag lets a Gmail filter on the To address catch every message, even when the sender edits the subject.</summary>
    public const string DefaultRecipient = "patilprashant6792.official+waymark-feedback@gmail.com";

    /// <summary>Every subject starts with this, the second thing a mail filter can match on.</summary>
    public const string DefaultSubjectPrefix = "[Waymark Feedback]";

    /// <summary>Some mail apps (Outlook on Windows among them) drop or cut a mailto link longer than about 2,000 characters.</summary>
    public const int DefaultMaxLinkLength = 1900;

    /// <summary>The most the message field accepts. Longer than a link can carry, so the full text can still be copied.</summary>
    public const int MaxMessageLength = 4000;

    // Address, prefix, a capped subject snippet and the footer take about 700 link characters at most, so any allowed length leaves
    // room for some of the message.
    private const int MinLinkLength = 1000;
    private const int MaxLinkLengthLimit = 32000;
    private const int MaxPrefixLength = 60;

    public static FeedbackOptions Default { get; } = new(DefaultRecipient, DefaultSubjectPrefix, DefaultMaxLinkLength);

    public static FeedbackOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var recipient = configuration["Feedback:Recipient"];
        recipient = string.IsNullOrWhiteSpace(recipient) ? DefaultRecipient : recipient.Trim();
        if (!IsPlainAddress(recipient))
        {
            throw new InvalidOperationException("Feedback:Recipient must be one email address, such as name@example.com.");
        }

        var prefix = configuration["Feedback:SubjectPrefix"];
        prefix = string.IsNullOrWhiteSpace(prefix) ? DefaultSubjectPrefix : prefix.Trim();
        if (prefix.Length > MaxPrefixLength || prefix.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"Feedback:SubjectPrefix must be one line of at most {MaxPrefixLength} characters."));
        }

        var maxLink = ReadLength(configuration["Feedback:MaxLinkLength"]);
        return new FeedbackOptions(recipient, prefix, maxLink);
    }

    /// <summary>
    /// One bare address. MailAddress also accepts a display name or comments ("Name &lt;a@b.c&gt;"), and characters that mean something in
    /// a link would change it, so both are refused.
    /// </summary>
    internal static bool IsPlainAddress(string value) =>
        MailAddress.TryCreate(value, out var parsed)
        && string.Equals(parsed.Address, value, StringComparison.Ordinal)
        && value.IndexOfAny(['?', '&', '#', '%', ' ', ',', ';']) < 0;

    private static int ReadLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultMaxLinkLength;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length is < MinLinkLength or > MaxLinkLengthLimit)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"Feedback:MaxLinkLength must be a number from {MinLinkLength} to {MaxLinkLengthLimit}."));
        }

        return length;
    }
}
