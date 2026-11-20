using System.Globalization;
using Microsoft.AspNetCore.Components;
using Waymark.Host.Components.Ui;
using Waymark.Host.Feedback;

namespace Waymark.Host.Components.Shared;

/// <summary>
/// Kind, message and an opt-in for version details, then a preview of the exact email. The mail link is built only on Preview, so
/// the hiding of secrets runs once instead of on every key press, and the owner sees what will leave this computer before it does.
/// Any change after the preview drops it, so the link always matches the text.
/// </summary>
public partial class FeedbackForm : LiveComponentBase
{
    private static readonly IReadOnlyList<WmOption<FeedbackKind>> KindOptions =
    [
        new(FeedbackKind.Bug, "Bug"),
        new(FeedbackKind.Idea, "Idea"),
        new(FeedbackKind.Question, "Question"),
        new(FeedbackKind.Praise, "Praise")
    ];

    private FeedbackKind _kind = FeedbackKind.Bug;
    private string _message = string.Empty;
    private bool _includeSystem = true;
    private FeedbackMail? _mail;
    private string? _error;
    private bool _busy;

    /// <summary>Set by the dialog: shows a Close button. The page leaves it out.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    [Inject]
    private FeedbackComposer Composer { get; set; } = default!;

    [Inject]
    private FeedbackOptions Options { get; set; } = default!;

    [Inject]
    private ILogger<FeedbackForm> Logger { get; set; } = default!;

    private bool CanPreview => !_busy && !string.IsNullOrWhiteSpace(_message);

    private string Counter => string.Create(CultureInfo.InvariantCulture, $"{_message.Length} / {FeedbackOptions.MaxMessageLength}");

    private void OnKindChanged(FeedbackKind kind)
    {
        _kind = kind;
        _mail = null;
    }

    private void OnMessageChanged(string? message)
    {
        _message = message ?? string.Empty;
        _mail = null;
        _error = null;
    }

    private void OnSystemChanged(bool include)
    {
        _includeSystem = include;
        _mail = null;
    }

    private async Task PreviewAsync()
    {
        if (!CanPreview)
        {
            return;
        }

        _error = null;
        _busy = true;
        try
        {
            _mail = await Composer.ComposeAsync(new FeedbackDraft(_kind, _message, _includeSystem), Cancel);
        }
        catch (OperationCanceledException)
        {
            // The form was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The feedback email could not be prepared");
            _error = "The email could not be prepared. It is written to the log, under Audit, Live, Errors.";
        }
        finally
        {
            _busy = false;
        }
    }
}
