using System.Text;
using Waymark.Core.Security;

namespace Waymark.Host.Feedback;

/// <summary>
/// Turns the feedback form into an email the owner's own mail app sends: a mailto link with the recipient, a subject that always starts
/// with the configured prefix (so the mails can be filtered into one folder) and the body. Nothing is sent from here. The text leaves
/// this computer, so known secrets and secrets with a recognisable shape are hidden first.
/// </summary>
public sealed class FeedbackComposer(IOutboundTextScrubber scrubber, FeedbackOptions options, FeedbackSystemInfo system)
{
    private const string Crlf = "\r\n";
    private const int SubjectSnippetLength = 60;
    private const int MaxEncodedSnippetLength = 120;
    private const string Ellipsis = "...";
    private const string ShortenedNote = "[The message was shortened to fit the email link.]";

    public async Task<FeedbackMail> ComposeAsync(FeedbackDraft draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var original = Normalize(draft.Message);
        if (original.Length == 0)
        {
            throw new ArgumentException("The message is empty.", nameof(draft));
        }

        if (original.Length > FeedbackOptions.MaxMessageLength)
        {
            throw new ArgumentException("The message is longer than the form allows.", nameof(draft));
        }

        // The guard skips its own hiding when the owner switched it off for tool results. This text goes to someone else, so the
        // shape scan always runs here as well; running it twice is harmless because markers do not match any rule.
        var known = await scrubber.HideKnownSecretsAsync(original, ct);
        var message = SensitiveDataScanner.Redact(known).Text;
        var hidden = !string.Equals(message, original, StringComparison.Ordinal);

        var subject = Subject(draft.Kind, message);
        var footer = Footer(draft, hidden);
        var body = Body(message, footer);
        var link = Mailto(subject, body);
        var shortened = false;
        if (link.Length > options.MaxLinkLength)
        {
            link = ShortenedLink(subject, message, footer);
            shortened = true;
        }

        var copy = "To: " + options.Recipient + Crlf + "Subject: " + subject + Crlf + Crlf + body;
        return new FeedbackMail(options.Recipient, subject, body, link, copy, shortened, hidden);
    }

    /// <summary>
    /// The longest start of the message whose link still fits. Percent-encoding makes one character cost 1 to 12 link characters, so the
    /// cut is searched instead of estimated. When not even the footer fits, the link carries no message text at all.
    /// </summary>
    private string ShortenedLink(string subject, string message, string footer)
    {
        string LinkFor(int length) =>
            Mailto(subject, Body(Cut(message, length) + Ellipsis + Crlf + Crlf + ShortenedNote, footer));

        var low = 0;
        var high = message.Length;
        while (low < high)
        {
            var middle = low + ((high - low + 1) / 2);
            if (LinkFor(middle).Length <= options.MaxLinkLength)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return LinkFor(low);
    }

    /// <summary>Cuts without splitting a surrogate pair, so the link never carries half an emoji.</summary>
    private static string Cut(string text, int length)
    {
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length].TrimEnd();
    }

    /// <summary>
    /// The prefix, the kind and the start of the first line. The snippet is also capped by its encoded size: sixty emoji or CJK
    /// characters would take hundreds of link characters, and the subject is never shortened later, only the message is.
    /// </summary>
    private string Subject(FeedbackKind kind, string message)
    {
        var firstLine = message.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? string.Empty;
        var snippet = string.Join(' ', firstLine.Split(' ', '\t').Where(word => word.Length > 0));
        var length = Math.Min(snippet.Length, SubjectSnippetLength);
        while (length > 0 && Uri.EscapeDataString(Cut(snippet, length)).Length > MaxEncodedSnippetLength)
        {
            length--;
        }

        if (length < snippet.Length)
        {
            snippet = Cut(snippet, length) + Ellipsis;
        }

        return options.SubjectPrefix + " " + kind + ": " + snippet;
    }

    private string Footer(FeedbackDraft draft, bool hidden)
    {
        var footer = new StringBuilder();
        footer.Append("--").Append(Crlf);
        footer.Append("Kind: ").Append(draft.Kind).Append(Crlf);
        if (draft.IncludeSystemInfo)
        {
            footer.Append("Waymark: ").Append(system.Version).Append(Crlf);
            footer.Append("OS: ").Append(system.Os).Append(Crlf);
            footer.Append(".NET: ").Append(system.Runtime).Append(Crlf);
        }

        if (hidden)
        {
            footer.Append("Note: text that looked like a secret was replaced by ").Append(SensitiveDataScanner.MarkerPrefix).Append("...] markers.").Append(Crlf);
        }

        footer.Append("Sent from the Waymark admin page.");
        return footer.ToString();
    }

    // RFC 6068: line breaks in a mailto body are CRLF.
    private static string Body(string message, string footer) => message.Replace("\n", Crlf, StringComparison.Ordinal) + Crlf + Crlf + footer;

    /// <summary>
    /// Uri.EscapeDataString writes a space as %20; form encoding would write '+', which mail apps show as a plus. The local part of the
    /// address is encoded too, so the '+' of a tagged address is not read as a space; the '@' stays as it is.
    /// </summary>
    private string Mailto(string subject, string body)
    {
        var at = options.Recipient.LastIndexOf('@');
        var address = Uri.EscapeDataString(options.Recipient[..at]) + options.Recipient[at..];
        return "mailto:" + address + "?subject=" + Uri.EscapeDataString(subject) + "&body=" + Uri.EscapeDataString(body);
    }

    /// <summary>One kind of line break, no control characters except tabs, and no trailing blank space.</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var unified = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var kept = new StringBuilder(unified.Length);
        foreach (var c in unified)
        {
            if (c is '\n' or '\t' || !char.IsControl(c))
            {
                kept.Append(c);
            }
        }

        return kept.ToString().Trim();
    }
}
