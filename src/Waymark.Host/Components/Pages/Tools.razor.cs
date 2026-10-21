using Microsoft.AspNetCore.Components;
using MudBlazor;
using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Tools;
using Waymark.Host.Components.Shared;
using Waymark.Host.Ui;

namespace Waymark.Host.Components.Pages;

public partial class Tools : LiveComponentBase
{
    private const string DeletePrefix = "delete";
    private const string ReadOnlyProfile = "readonly";
    private const string EditProfile = "edit";
    private const string FullProfile = "full";
    private const string CustomProfile = "custom";

    private IReadOnlyList<ToolRow> _rows = [];
    private string? _error;
    private bool _busy;
    private string _search = string.Empty;
    private string _kind = "all";
    private string _state = "all";

    [Inject]
    private ToolCatalog Catalog { get; set; } = default!;

    [Inject]
    private IToolSettingsStore Settings { get; set; } = default!;

    [Inject]
    private IAuditStore Audit { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private ILogger<Tools> Logger { get; set; } = default!;

    private int EnabledCount => _rows.Count(r => r.Enabled);

    private int EnabledTokens => _rows.Where(r => r.Enabled).Sum(r => r.Tokens);

    private int TotalTokens => _rows.Sum(r => r.Tokens);

    private string CurrentProfile
    {
        get
        {
            if (Matches(ReadOnlyProfile))
            {
                return ReadOnlyProfile;
            }

            if (Matches(EditProfile))
            {
                return EditProfile;
            }

            return Matches(FullProfile) ? FullProfile : CustomProfile;
        }
    }

    // Only what the filter lets through is listed; the profile and the token figures above still count every tool.
    private IEnumerable<ToolRow> VisibleRows => _rows.Where(RowMatchesFilter);

    private int VisibleCount => VisibleRows.Count();

    private IEnumerable<IGrouping<string, ToolRow>> Groups => VisibleRows.GroupBy(r => r.Tool.Module);

    private bool RowMatchesFilter(ToolRow row)
    {
        var text = _search.Trim();
        if (text.Length > 0
            && !row.Tool.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            && !row.Tool.Module.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if ((_kind == "read" && row.Tool.Gate != ToolGate.Read) || (_kind == "confirm" && row.Tool.Gate != ToolGate.Confirm))
        {
            return false;
        }

        return _state switch
        {
            "on" => row.Enabled,
            "off" => !row.Enabled,
            _ => true
        };
    }

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private static bool WantedBy(string profile, ToolRow row) => profile switch
    {
        ReadOnlyProfile => row.Tool.Gate == ToolGate.Read,
        EditProfile => !row.Tool.Name.Contains(DeletePrefix, StringComparison.OrdinalIgnoreCase),
        _ => true
    };

    private bool Matches(string profile) => _rows.Count > 0 && _rows.All(r => r.Enabled == WantedBy(profile, r));

    private async Task LoadAsync()
    {
        try
        {
            _rows = await Catalog.ListAsync(Cancel);
            _error = null;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The tool list could not be loaded");
            _error = "The tools could not be loaded.";
        }
    }

    private async Task ApplyProfileAsync(string profile)
    {
        if (profile == CurrentProfile || _busy)
        {
            return;
        }

        var turnOn = _rows.Where(r => WantedBy(profile, r) && !r.Enabled).Select(r => r.Tool.Name).ToList();
        var turnOff = _rows.Where(r => !WantedBy(profile, r) && r.Enabled).Select(r => r.Tool.Name).ToList();
        if (turnOn.Any(IsConfirmTool))
        {
            var confirmed = await ConfirmDialogs.AskAsync(
                Dialogs,
                "Let Claude change files?",
                $"This switches on {turnOn.Count(IsConfirmTool)} tools that create, edit, move or delete files in your workspaces. Each change is still recorded in the audit trail.",
                "Switch on",
                destructive: false);
            if (!confirmed)
            {
                return;
            }
        }

        await ChangeAsync(turnOn, turnOff, $"config.profile.{profile}");
    }

    private async Task ToggleAsync(ToolRow row, bool enabled)
    {
        if (enabled && row.Tool.Gate == ToolGate.Confirm)
        {
            var confirmed = await ConfirmDialogs.AskAsync(
                Dialogs,
                $"Switch on {row.Tool.Name}?",
                "This tool changes files in your workspaces. It adds about " + DisplayFormat.Count(row.Tokens) + " tokens to every conversation.",
                "Switch on",
                destructive: false);
            if (!confirmed)
            {
                await LoadAsync();
                return;
            }
        }

        List<string> on = enabled ? [row.Tool.Name] : [];
        List<string> off = enabled ? [] : [row.Tool.Name];
        await ChangeAsync(on, off, enabled ? "config.tool.on" : "config.tool.off");
    }

    private bool IsConfirmTool(string name) =>
        _rows.Any(r => r.Tool.Name == name && r.Tool.Gate == ToolGate.Confirm);

    private async Task ChangeAsync(IReadOnlyCollection<string> turnOn, IReadOnlyCollection<string> turnOff, string auditAction)
    {
        if (turnOn.Count == 0 && turnOff.Count == 0)
        {
            return;
        }

        _busy = true;
        try
        {
            await WriteAsync(turnOn, turnOff, auditAction);
            await LoadAsync();
            _lastChange = (turnOn.ToList(), turnOff.ToList());
            Snackbar.Add(
                "Saved. Claude sees the change the next time it connects.",
                Severity.Success,
                options =>
                {
                    options.Action = "Undo";
                    options.OnClick = _ => InvokeAsync(UndoAsync);
                });
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The tool switches could not be saved");
            _error = "The change could not be saved.";
            await LoadAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private (List<string> On, List<string> Off)? _lastChange;

    private async Task UndoAsync()
    {
        if (_lastChange is not { } change)
        {
            return;
        }

        _lastChange = null;
        _busy = true;
        try
        {
            await WriteAsync(change.Off, change.On, "config.undo");
            await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task WriteAsync(IReadOnlyCollection<string> turnOn, IReadOnlyCollection<string> turnOff, string auditAction)
    {
        var now = Time.GetUtcNow();
        if (turnOn.Count > 0)
        {
            await Settings.SetManyAsync(turnOn, true, AuthDefaults.AdminActor, now, Cancel);
        }

        if (turnOff.Count > 0)
        {
            await Settings.SetManyAsync(turnOff, false, AuthDefaults.AdminActor, now, Cancel);
        }

        var target = string.Join(',', turnOn.Select(n => "+" + n).Concat(turnOff.Select(n => "-" + n)));
        await Audit.AppendAsync(AuditEntries.Create(Time, AuditCategory.Config, auditAction, AuthDefaults.AdminActor, target), Cancel);
    }

    private string ToolTip(ToolRow row) =>
        row.Tool.Gate == ToolGate.Confirm ? "Changes files. Off until you switch it on." : "Only reads.";
}
