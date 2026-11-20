using System.Runtime.InteropServices;
using Waymark.Host.Ui;

namespace Waymark.Host.Feedback;

/// <summary>What the feedback is about. The name goes into the subject, so a mail filter or a search can tell them apart.</summary>
public enum FeedbackKind
{
    Bug,
    Idea,
    Question,
    Praise
}

/// <summary>What the owner filled in on the feedback form.</summary>
public sealed record FeedbackDraft(FeedbackKind Kind, string Message, bool IncludeSystemInfo);

/// <summary>
/// The email, ready for the owner's mail app. <paramref name="Body"/> and <paramref name="CopyText"/> hold the whole message;
/// <paramref name="MailtoUri"/> may carry a shortened one (<paramref name="Shortened"/>) because mail apps refuse very long links.
/// </summary>
public sealed record FeedbackMail(
    string Recipient,
    string Subject,
    string Body,
    string MailtoUri,
    string CopyText,
    bool Shortened,
    bool SecretsHidden);

/// <summary>The facts about this computer a bug report needs. No paths, names, addresses or workspace details.</summary>
public sealed record FeedbackSystemInfo(string Version, string Os, string Runtime)
{
    public static FeedbackSystemInfo ForThisProcess() =>
        new(AppInfo.VersionWithBuild, RuntimeInformation.OSDescription.Trim(), RuntimeInformation.FrameworkDescription.Trim());
}

/// <summary>Hides the secrets Waymark knows by value (the vault) in text that is about to leave this computer.</summary>
public interface IOutboundTextScrubber
{
    Task<string> HideKnownSecretsAsync(string text, CancellationToken ct);
}
