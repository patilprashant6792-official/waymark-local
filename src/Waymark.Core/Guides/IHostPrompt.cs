namespace Waymark.Core.Guides;

/// <summary>The owner's text for the overview. <see cref="ChangedUtc"/> and <see cref="ChangedBy"/> are null while the owner has written nothing.</summary>
public sealed record HostPromptText(string Content, DateTimeOffset? ChangedUtc, string? ChangedBy);

/// <summary>
/// The overview every agent can load first. Waymark ships the template (what each tool group does, how the shells and long runs work, the live setup);
/// the owner's own instructions are rendered inside it. This holds the owner's text and renders the whole.
/// </summary>
public interface IHostPrompt
{
    Task<HostPromptText> GetOwnerTextAsync(CancellationToken ct);

    /// <summary>
    /// Saves the owner's text; empty text removes it. Throws <see cref="ArgumentException"/> for text over the limit or text that holds a secret.
    /// </summary>
    Task SaveOwnerTextAsync(string text, string changedBy, CancellationToken ct);

    /// <summary>The hash of the overview without the live setup. Cheap, so it can be asked on every call.</summary>
    Task<string> HashAsync(HostTemplate template, CancellationToken ct);

    /// <summary>The overview as an agent receives it, with the live setup filled in.</summary>
    Task<string> RenderAsync(HostTemplate template, CancellationToken ct);
}

/// <summary>What is enabled right now, written for an agent. It is filled into the overview when it is sent.</summary>
public interface ISetupSummary
{
    Task<string> DescribeAsync(CancellationToken ct);
}
