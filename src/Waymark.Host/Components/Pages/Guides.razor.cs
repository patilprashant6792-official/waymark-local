using Microsoft.AspNetCore.Components;
using MudBlazor;
using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Guides;
using Waymark.Core.Tools;
using Waymark.Host.Components.Shared;
using Waymark.Host.Ui;

namespace Waymark.Host.Components.Pages;

public partial class Guides : LiveComponentBase
{
    private const string Placeholder =
        "For example:\n\n" +
        "## How I work\n- Small commits, one idea each. Never push without asking.\n\n" +
        "## Updating my installed copy\n- The repo has install.cmd. To update my installed copy, run it with cmd_shell in the Waymark workspace, then stop and wait for me to say it finished. After that, check that it is running.\n\n" +
        "## Always ask first\n- Before adding a package, deleting a file or touching a migration.";

    private IReadOnlyList<ToolGuide> _groupGuides = [];
    private IReadOnlyList<GuideDelivery> _deliveries = [];
    private readonly Dictionary<string, int> _tokens = new(StringComparer.Ordinal);
    private HostPromptText _saved = new(string.Empty, null, null);
    private ToolGuide? _overview;
    private string _draft = string.Empty;
    private int _draftTokens;
    private int _overviewTokens;
    private string _preview = string.Empty;
    private bool _showPreview;
    private string? _error;
    private bool _busy;

    [Inject]
    private IGuideCatalog Catalog { get; set; } = default!;

    [Inject]
    private IHostPrompt Prompt { get; set; } = default!;

    [Inject]
    private IGuideStore Store { get; set; } = default!;

    [Inject]
    private IContextMeter Meter { get; set; } = default!;

    [Inject]
    private IAuditStore Audit { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private ILogger<Guides> Logger { get; set; } = default!;

    private int DraftChars => GuideRules.Normalize(_draft).Length;

    private bool TooLong => DraftChars > HostPromptRules.MaxOwnerChars;

    private bool IsDirty => !string.Equals(GuideRules.Normalize(_draft), _saved.Content, StringComparison.Ordinal);

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        Revert();
    }

    private int TokensOf(ToolGuide guide) => _tokens.TryGetValue(guide.Module, out var count) ? count : 0;

    // What an agent receives is the guide inside its wrapper, so that is what the count measures.
    private int CountSent(ToolGuide guide) => Meter.CountTokens(GuideRules.Deliver(guide.Module, guide.Content));

    private string LastChange() =>
        _saved.ChangedUtc is { } at ? $"Saved {DisplayFormat.Ago(at, Time.GetUtcNow())}" : "Nothing saved yet";

    private string HolderText()
    {
        if (_overview is null)
        {
            return string.Empty;
        }

        var current = _deliveries.Count(d => d.Module == _overview.Module && d.ContentHash == _overview.Hash);
        var older = _deliveries.Count(d => d.Module == _overview.Module && d.ContentHash != _overview.Hash);
        if (current + older == 0)
        {
            return "No agent has loaded the overview yet.";
        }

        return older == 0
            ? $"{Clients(current)} hold the current overview."
            : $"{Clients(current)} hold the current overview; {older} hold an older one and are told on their next call.";
    }

    private string GroupHolders(ToolGuide guide)
    {
        var current = _deliveries.Count(d => d.Module == guide.Module && d.ContentHash == guide.Hash);
        var older = _deliveries.Count(d => d.Module == guide.Module && d.ContentHash != guide.Hash);
        if (current + older == 0)
        {
            return "No agent yet";
        }

        return older == 0 ? Clients(current) : $"{Clients(current)}, and {older} with an older text";
    }

    private static string Clients(int count) => count == 1 ? "1 client" : $"{count} clients";

    private void OnInput(string? value)
    {
        _draft = value ?? string.Empty;
        _draftTokens = Meter.CountTokens(_draft);
    }

    private void Revert()
    {
        _draft = _saved.Content;
        _draftTokens = Meter.CountTokens(_draft);
    }

    private async Task LoadAsync()
    {
        try
        {
            _saved = await Prompt.GetOwnerTextAsync(Cancel);
            var guides = await Catalog.ListAsync(Cancel);
            _deliveries = await Store.ListDeliveriesAsync(Cancel);
            _overview = guides.FirstOrDefault(g => GuideRules.IsOverview(g.Module));
            _groupGuides = guides.Where(g => !GuideRules.IsOverview(g.Module)).ToList();
            _tokens.Clear();
            foreach (var guide in guides)
            {
                _tokens[guide.Module] = CountSent(guide);
            }

            _overviewTokens = _overview is null ? 0 : TokensOf(_overview);
            _preview = _overview is null ? string.Empty : GuideRules.Deliver(_overview.Module, _overview.Content);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The host prompt could not be loaded");
            _error = "The host prompt could not be loaded.";
        }
    }

    private async Task SaveAsync()
    {
        _error = null;
        _busy = true;
        try
        {
            await Prompt.SaveOwnerTextAsync(_draft, AuthDefaults.AdminActor, Cancel);
            await Audit.AppendAsync(AuditEntries.Create(Time, AuditCategory.Config, "config.hostprompt.save", AuthDefaults.AdminActor, HostPromptRules.StoreKey), Cancel);
            await LoadAsync();
            Revert();
            Snackbar.Add("Saved. Agents that hold the old text are told on their next call.", Severity.Success);
        }
        catch (ArgumentException ex)
        {
            _error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The host prompt could not be saved");
            _error = "Your instructions could not be saved.";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ClearAsync()
    {
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Clear your instructions?",
            "Your text is deleted and the overview goes back to Waymark's own text. Agents that hold the current overview are told on their next call.",
            "Clear",
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        _error = null;
        _busy = true;
        try
        {
            await Prompt.SaveOwnerTextAsync(string.Empty, AuthDefaults.AdminActor, Cancel);
            await Audit.AppendAsync(AuditEntries.Create(Time, AuditCategory.Config, "config.hostprompt.clear", AuthDefaults.AdminActor, HostPromptRules.StoreKey), Cancel);
            await LoadAsync();
            Revert();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The host prompt could not be cleared");
            _error = "Your instructions could not be cleared.";
        }
        finally
        {
            _busy = false;
        }
    }
}
