using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Waymark.Core.Audit;
using Waymark.Host.Components.Shared;
using Waymark.Host.Mcp;
using Waymark.Host.Ui;

namespace Waymark.Host.Components.Pages;

public partial class Audit : LiveComponentBase
{
    private const int PageSize = 50;
    private const int ExportLimit = 500;
    private static readonly TimeSpan TailEvery = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions ExportOptions = new() { WriteIndented = true };

    private readonly List<long?> _cursors = [null];
    private IReadOnlyList<StoredAuditEntry> _rows = [];
    private IReadOnlyList<string> _tools = [];
    private IReadOnlyList<string> _workspaces = [];
    private StoredAuditEntry? _selected;
    private string _text = string.Empty;
    private string _tool = string.Empty;
    private string _workspace = string.Empty;
    private string _outcome = string.Empty;
    private string _kind = string.Empty;
    private string _range = "24h";
    private int _page;
    private int _total;
    private bool _live = true;
    private string? _error;
    private PayloadSettings _keep = new(true, ToolPayloadLimits.DefaultDays);
    private ToolPayloadStats _keepStats = new(0, 0);
    private bool _confirmClear;
    private string? _keepNote;

    [Inject]
    private IAuditStore Store { get; set; } = default!;

    [Inject]
    private IJSRuntime Js { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private ILogger<Audit> Logger { get; set; } = default!;

    [Inject]
    private ToolPayloadService Payloads { get; set; } = default!;

    private int FirstShown => _rows.Count == 0 ? 0 : (_page * PageSize) + 1;

    private int LastShown => (_page * PageSize) + _rows.Count;

    private bool HasOlder => LastShown < _total;

    private bool IsTailing => _live && _page == 0;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _tools = await Store.DistinctActionsAsync(AuditCategory.Tool, Cancel);
            _workspaces = await Store.DistinctTargetsAsync(AuditCategory.Tool, Cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The audit filters could not be loaded");
        }

        await LoadKeepAsync();
        await LoadAsync();
        _ = TailAsync();
    }

    private AuditQuery BuildQuery(long? beforeId, int limit)
    {
        var category = _kind switch
        {
            "tool" => AuditCategory.Tool,
            "approval" => AuditCategory.Approval,
            "auth" => AuditCategory.Auth,
            "config" => AuditCategory.Config,
            _ => (AuditCategory?)null
        };
        var outcome = _outcome switch
        {
            "ok" => AuditOutcome.Ok,
            "denied" => AuditOutcome.Denied,
            "error" => AuditOutcome.Error,
            _ => (AuditOutcome?)null
        };
        DateTimeOffset? since = _range switch
        {
            "1h" => Time.GetUtcNow().AddHours(-1),
            "24h" => Time.GetUtcNow().AddHours(-24),
            "7d" => Time.GetUtcNow().AddDays(-7),
            _ => null
        };
        return new AuditQuery(category, outcome, beforeId, limit, EmptyToNull(_tool), EmptyToNull(_workspace), EmptyToNull(_text), since);
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private async Task LoadAsync()
    {
        try
        {
            var query = BuildQuery(_cursors[_page], PageSize);
            _rows = await Store.QueryAsync(query, Cancel);
            _total = await Store.CountAsync(query, Cancel);
            _error = null;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The audit trail could not be loaded");
            _error = "The audit trail could not be loaded.";
        }
    }

    private async Task ApplyFiltersAsync()
    {
        _page = 0;
        _cursors.Clear();
        _cursors.Add(null);
        await LoadAsync();
    }

    private async Task OlderAsync()
    {
        if (!HasOlder || _rows.Count == 0)
        {
            return;
        }

        if (_cursors.Count == _page + 1)
        {
            _cursors.Add(_rows[^1].Id);
        }

        _page++;
        await LoadAsync();
    }

    private async Task NewerAsync()
    {
        if (_page == 0)
        {
            return;
        }

        _page--;
        await LoadAsync();
    }

    private async Task OnSearchKeyAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            await ApplyFiltersAsync();
        }
    }

    private async Task ClearFiltersAsync()
    {
        _text = string.Empty;
        _tool = string.Empty;
        _workspace = string.Empty;
        _outcome = string.Empty;
        _kind = string.Empty;
        _range = "24h";
        await ApplyFiltersAsync();
    }

    private async Task ExportAsync()
    {
        try
        {
            var rows = await Store.QueryAsync(BuildQuery(null, ExportLimit), Cancel);
            var items = rows.Select(r => new
            {
                reference = r.Id,
                timestampUtc = r.Entry.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                category = r.Entry.Category.ToString(),
                actor = r.Entry.Actor,
                action = r.Entry.Action,
                target = r.Entry.Target,
                argumentNames = r.Entry.ArgsRedacted,
                durationMs = r.Entry.DurationMs,
                resultBytes = r.Entry.ResultBytes,
                outcome = r.Entry.Outcome.ToString(),
                error = r.Entry.Error
            });
            var name = string.Create(CultureInfo.InvariantCulture, $"waymark-audit-{Time.GetUtcNow():yyyyMMdd-HHmmss}.json");
            await Js.InvokeVoidAsync("waymark.download", name, JsonSerializer.Serialize(items, ExportOptions), "application/json");
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException)
        {
            Logger.LogDebug(ex, "The audit export was not delivered");
        }
    }

    private void Open(StoredAuditEntry row) => _selected = row;

    private void Close() => _selected = null;

    private void OnDrawerKey(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            Close();
        }
    }

    private async Task TailAsync()
    {
        using var timer = new PeriodicTimer(TailEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(Cancel))
            {
                if (IsTailing && _selected is null)
                {
                    await InvokeAsync(async () =>
                    {
                        await LoadAsync();
                        StateHasChanged();
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (ObjectDisposedException)
        {
            // The circuit ended.
        }
    }


    private void OnRowKey(KeyboardEventArgs e, StoredAuditEntry row)
    {
        if (e.Key == "Enter")
        {
            Open(row);
        }
    }
    private static string ClientOf(AuditEntry entry) => entry.Actor ?? "-";

    private async Task LoadKeepAsync()
    {
        try
        {
            _keep = await Payloads.GetSettingsAsync(Cancel);
            _keepStats = await Store.PayloadStatsAsync(Cancel);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The settings for keeping inputs and outputs could not be loaded");
        }
    }

    private Task OnKeepToggledAsync(ChangeEventArgs e) => SaveKeepAsync(e.Value is true, _keep.Days);

    private Task OnKeepDaysChangedAsync(ChangeEventArgs e) =>
        int.TryParse(e.Value?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var days) ? SaveKeepAsync(_keep.Enabled, days) : Task.CompletedTask;

    private async Task SaveKeepAsync(bool enabled, int days)
    {
        try
        {
            await Payloads.SaveSettingsAsync(enabled, days, Cancel);
            _keepNote = enabled
                ? $"Inputs and outputs of new calls are kept for {days} days."
                : "Inputs and outputs of new calls are no longer kept. Those already kept stay until they expire or you delete them.";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The settings for keeping inputs and outputs could not be saved");
            _error = "The setting could not be saved.";
        }

        await LoadKeepAsync();
    }

    private async Task ClearKeepAsync()
    {
        if (!_confirmClear)
        {
            _confirmClear = true;
            return;
        }

        _confirmClear = false;
        try
        {
            var removed = await Payloads.ClearAsync(Cancel);
            _keepNote = $"Deleted the kept input and output of {removed} calls.";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The kept inputs and outputs could not be deleted");
            _error = "They could not be deleted.";
        }

        await LoadKeepAsync();
    }
}
