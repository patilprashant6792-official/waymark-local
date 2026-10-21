using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Waymark.Core.Approvals;
using Waymark.Core.Commands;
using Waymark.Core.Audit;
using Waymark.Core.Code;
using Waymark.Core.Edge;
using Waymark.Core.NuGetIndex;
using Waymark.Core.Events;
using Waymark.Core.Workspaces;
using Waymark.Core.Auth;
using Waymark.Core.Settings;
using Waymark.Host.Auth;
using Waymark.Host.Components.Shared;
using Waymark.Host.Edge;
using Waymark.Host.NuGetAdmin;
using Waymark.Host.Ui;

namespace Waymark.Host.Components.Pages;

public partial class Home : LiveComponentBase
{
    private const int RecentPackages = 4;
    private const int RecentCommands = 5;
    private const int ReloadEveryTicks = 5;
    private const int RecentCallRows = 5;

    private readonly Dictionary<long, CodeIndexProgress> _progress = [];
    private IDisposable? _progressSubscription;
    private IReadOnlyList<Approval> _pending = [];
    private IReadOnlyList<StoredAuditEntry> _recent = [];
    private IReadOnlyList<Workspace> _workspaces = [];
    private string? _pathRoot;
    private IReadOnlyDictionary<long, CodeIndexStats> _stats = new Dictionary<long, CodeIndexStats>();
    private IReadOnlyList<ToolRow> _tools = [];
    private NuGetCacheStats? _nuget;
    private IReadOnlyList<NuGetCacheEntry> _nugetRecent = [];
    private IReadOnlyList<CommandRun> _commandsWaiting = [];
    private IReadOnlyList<CommandRun> _commandsRecent = [];
    private string? _error;
    private bool _deciding;

    private const string GridId = "wm-dashboard-grid";

    private DashboardLayout _layout = DashboardLayout.Default(workspacesFirst: false);
    private bool _customLayout;
    private bool _editing;
    private bool _savingLayout;
    private string? _layoutError;
    private GridCommand _gridCommand;
    private ContextUsageSnapshot? _tokens;
    private int _callStatsRunning;
    private int _callStatsAgain;

    /// <summary>What the script must do for the grid after the next render, once the new markup is in place.</summary>
    private enum GridCommand
    {
        None,
        Begin,
        End
    }

    [Inject]
    private IApprovalStore Approvals { get; set; } = default!;

    [Inject]
    private ApprovalService Decisions { get; set; } = default!;

    [Inject]
    private IAuditStore Audit { get; set; } = default!;

    [Inject]
    private IWorkspaceService WorkspaceService { get; set; } = default!;

    [Inject]
    private ICodeIndexStore CodeIndex { get; set; } = default!;

    [Inject]
    private ToolCatalog Catalog { get; set; } = default!;

    [Inject]
    private ShellStatus Status { get; set; } = default!;

    [Inject]
    private EdgeOptions Edge { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private NuGetCacheService NuGetPackages { get; set; } = default!;

    [Inject]
    private ICommandRuns CommandRuns { get; set; } = default!;

    [Inject]
    private ILogger<Home> Logger { get; set; } = default!;

    [Inject]
    private IConfigStore Config { get; set; } = default!;

    [Inject]
    private IJSRuntime Js { get; set; } = default!;

    [Inject]
    private ContextUsageService ContextUsage { get; set; } = default!;

    private Approval? First => _pending.Count > 0 ? _pending[0] : null;

    private int EnabledTools => _tools.Count(t => t.Enabled);

    private int EnabledTokens => _tools.Where(t => t.Enabled).Sum(t => t.Tokens);

    private int AllTokens => Math.Max(1, _tools.Sum(t => t.Tokens));

    private int IndexedFiles => _stats.Values.Sum(s => s.Files);

    private int IndexedTypes => _stats.Values.Sum(s => s.Types);

    private CodeIndexProgress? Indexing =>
        _progress.Values.FirstOrDefault(p => p.Phase is CodeIndexPhase.Started or CodeIndexPhase.Running);

    private int ToolPercent => (int)Math.Clamp(EnabledTokens * 100L / AllTokens, 0, 100);

    private (StatusState State, string Text) CodeIndexState =>
        Indexing is { } running ? (StatusState.Working, $"Indexing {DisplayFormat.Count(running.FilesProcessed)} of {DisplayFormat.Count(running.FilesTotal)}")
        : _workspaces.Count == 0 ? (StatusState.Off, "No workspaces")
        : (StatusState.Healthy, "Ready");

    // Things worth a look, shown as chips under the title; empty when everything is fine.
    private IReadOnlyList<AttentionItem> Attention
    {
        get
        {
            var items = new List<AttentionItem>();
            if (Status.Paused)
            {
                items.Add(new AttentionItem("Server is paused", null, StatusState.Attention));
            }

            if (Status.Tunnel.Phase == TunnelPhase.Failed)
            {
                items.Add(new AttentionItem("Tunnel failed", "setup", StatusState.Failed));
            }

            if (_nuget is { Failed: > 0 } nuget)
            {
                items.Add(new AttentionItem($"{nuget.Failed} NuGet {(nuget.Failed == 1 ? "package" : "packages")} failed", "nuget", StatusState.Failed));
            }

            return items;
        }
    }

    private static string TileClass(StatusState state) => state switch
    {
        StatusState.Healthy => "ok",
        StatusState.Attention => "warn",
        StatusState.Failed => "err",
        StatusState.Working => "info",
        _ => "off"
    };

    private static (StatusState State, string Text) PackageState(NuGetIndexState state) => state switch
    {
        NuGetIndexState.Ready => (StatusState.Healthy, "Ready"),
        NuGetIndexState.Failed => (StatusState.Failed, "Failed"),
        _ => (StatusState.Working, "Building")
    };

    private string FilesOf(Workspace workspace) =>
        _stats.TryGetValue(workspace.Id, out var stats) ? $"{DisplayFormat.Count(stats.Files)} files" : "not indexed";

    private sealed record AttentionItem(string Text, string? Href, StatusState State);

    private static string CommandLabel(CommandRun run) =>
        CommandCatalog.TryGet(run.Operation, out var info) && info is not null ? info.Label : run.Operation;

    private static (StatusState State, string Text) CommandState(CommandRun run) => run.Status switch
    {
        CommandRunStatus.Succeeded => (StatusState.Healthy, "Succeeded"),
        CommandRunStatus.Failed => (StatusState.Failed, "Failed"),
        CommandRunStatus.TimedOut => (StatusState.Failed, "Timed out"),
        CommandRunStatus.Running => (StatusState.Working, "Running"),
        CommandRunStatus.Queued => (StatusState.Attention, "Queued"),
        CommandRunStatus.WaitingApproval => (StatusState.Attention, "Waiting for you"),
        CommandRunStatus.Denied => (StatusState.Off, "Denied"),
        _ => (StatusState.Off, "Stopped")
    };

    private static string FirstLine(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 80 ? line : line[..80] + "...";
    }

    protected override async Task OnInitializedAsync()
    {
        await LoadLayoutAsync();
        await LoadAsync();
        RefreshOn<ApprovalRequested>(LoadAsync);
        RefreshOn<ApprovalDecided>(LoadAsync);
        RefreshOn<WorkspaceChanged>(LoadAsync);
        RefreshOn<NuGetCacheChanged>(LoadAsync);
        RefreshOn<CommandRunChanged>(LoadAsync);
        _progressSubscription = Events.Subscribe<CodeIndexProgress>(OnProgressAsync);
        Status.Changed += OnStatusChanged;
        _ = TickAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _pending = await Approvals.ListPendingAsync(Time.GetUtcNow(), Cancel);
            _recent = await Audit.QueryAsync(new AuditQuery(AuditCategory.Tool, null, null, RecentCallRows), Cancel);
            _workspaces = await WorkspaceService.ListAsync(Cancel);
            _pathRoot = WorkspacePathDisplay.CommonParent(_workspaces.Select(workspace => workspace.Path));
            if (!_customLayout && !_editing)
            {
                // Until the owner saves an arrangement, a missing first workspace keeps its card near the top.
                _layout = DashboardLayout.Default(workspacesFirst: _workspaces.Count == 0);
            }
            var stats = new Dictionary<long, CodeIndexStats>();
            foreach (var workspace in _workspaces)
            {
                stats[workspace.Id] = await CodeIndex.GetStatsAsync(workspace.Id, Cancel);
            }

            _stats = stats;
            _tools = await Catalog.ListAsync(Cancel);
            var overview = await NuGetPackages.GetOverviewAsync(Cancel);
            _nuget = overview.Stats;
            _nugetRecent = overview.Entries
                .OrderByDescending(entry => entry.Package.IndexedUtc ?? entry.Package.StartedUtc)
                .Take(RecentPackages)
                .ToList();
            _commandsWaiting = await CommandRuns.ListPendingAsync(Cancel);
            _commandsRecent = await CommandRuns.ListAsync(new CommandRunQuery(Limit: RecentCommands), Cancel);
            _tokens = await ContextUsage.GetAsync(Cancel);
            _error = null;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The Home page could not load its data");
            _error = "Some of this page could not be loaded. It will try again in a few seconds.";
        }
    }

    private string ShownPath(string path) => WorkspacePathDisplay.Relative(path, _pathRoot);

    private async Task LoadLayoutAsync()
    {
        try
        {
            var saved = await Config.GetAsync(SettingScope.Global, DashboardLayout.SettingKey, Cancel);
            if (saved is null)
            {
                return;
            }

            if (DashboardLayout.TryParse(saved.ValueJson, out var layout))
            {
                _layout = layout;
                _customLayout = true;
                return;
            }

            Logger.LogWarning("The saved dashboard arrangement could not be read, so the default arrangement is shown.");
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The saved dashboard arrangement could not be loaded, so the default arrangement is shown.");
        }
    }

    private static string CellStyle(CardPlacement card, int index) =>
        FormattableString.Invariant($"--w:{card.Width};--h:{card.Height};--o:{index}");

    private static string EditLabel(DashboardCardInfo info) =>
        $"{info.Title} card. Press Alt and an arrow key to move it, or Shift and an arrow key to resize it.";

    private void StartEditing()
    {
        _layoutError = null;
        _editing = true;
        _gridCommand = GridCommand.Begin;
    }

    private void CancelEditing()
    {
        _layoutError = null;
        _editing = false;
        _gridCommand = GridCommand.End;
    }

    // Shows the default arrangement on screen. It is only kept when the owner presses Done.
    private async Task ResetLayoutAsync()
    {
        try
        {
            var defaults = DashboardLayout.Default(workspacesFirst: _workspaces.Count == 0);
            var applied = await Js.InvokeAsync<bool>("wmGrid.apply", Cancel, GridId, defaults.ToJson());
            _layoutError = applied ? null : "The default arrangement could not be shown. Close this bar with Cancel and try again.";
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            Logger.LogWarning(ex, "The default dashboard arrangement could not be shown");
            _layoutError = "The default arrangement could not be shown. Close this bar with Cancel and try again.";
        }
    }

    private async Task SaveLayoutAsync()
    {
        _savingLayout = true;
        _layoutError = null;
        try
        {
            var json = await Js.InvokeAsync<string?>("wmGrid.read", Cancel, GridId);
            if (!DashboardLayout.TryParse(json, out var layout))
            {
                _layoutError = "The arrangement could not be read, so nothing was saved. Try Done again.";
                return;
            }

            await Config.SetAsync(SettingScope.Global, DashboardLayout.SettingKey, layout.ToJson(), AuthDefaults.AdminActor, Cancel);
            _layout = layout;
            _customLayout = true;
            _editing = false;
            _gridCommand = GridCommand.End;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The dashboard arrangement could not be saved");
            _layoutError = "The arrangement could not be saved. It is still on screen, so you can press Done to try again.";
        }
        finally
        {
            _savingLayout = false;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var command = _gridCommand;
        if (command == GridCommand.None)
        {
            return;
        }

        _gridCommand = GridCommand.None;
        try
        {
            await Js.InvokeVoidAsync(command == GridCommand.Begin ? "wmGrid.begin" : "wmGrid.end", Cancel, GridId);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            Logger.LogWarning(ex, "The dashboard arrangement script could not be reached");
        }
    }

    private async Task DecideAsync(Approval approval, bool approve)
    {
        _deciding = true;
        try
        {
            var decided = await Decisions.DecideAsync(approval.Id, approve, Cancel);
            _error = decided ? null : "That request was no longer waiting.";
            await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        finally
        {
            _deciding = false;
        }
    }

    private string SecondsLeft(Approval approval)
    {
        var left = approval.ExpiresUtc - Time.GetUtcNow();
        var seconds = (int)Math.Max(0, left.TotalSeconds);
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    private static string DescribeScope(string scope)
    {
        var parts = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s switch
            {
                "mcp" => "use your Waymark tools",
                "offline_access" => "stay signed in for 30 days",
                _ => s
            });
        return string.Join(", ", parts);
    }

    private (StatusState State, string Text) TunnelState => Status.Tunnel.Phase switch
    {
        TunnelPhase.Online => (StatusState.Healthy, "Online"),
        TunnelPhase.Installing or TunnelPhase.Starting => (StatusState.Working, "Starting"),
        TunnelPhase.Failed => (StatusState.Failed, "Failed"),
        _ => (StatusState.Off, "Off")
    };

    // A short fingerprint of what each card shows. When it changes, the card's dot lights up for a few seconds, so a real
    // change reads differently from a reload that found nothing new.
    private string CardToken(string cardId) => cardId switch
    {
        DashboardCards.Server => $"{Status.Paused}|{Status.ActiveNow}",
        DashboardCards.Tunnel => $"{Status.Tunnel.Phase}|{Status.Tunnel.Domain}|{Status.Tunnel.Message}",
        DashboardCards.CodeIndex => $"{IndexedFiles}|{IndexedTypes}|{CodeIndexState.Text}|{_workspaces.Count}",
        DashboardCards.Tools => $"{EnabledTools}|{_tools.Count}|{EnabledTokens}",
        DashboardCards.Tokens => _tokens is not { } usage ? "-" : $"{usage.Session.Calls}|{usage.Session.Tokens}|{usage.Today.Tokens}|{usage.Recorded.Tokens}",
        DashboardCards.RecentCalls => string.Join(';', _recent.Select(call => $"{call.Entry.TimestampUtc}|{call.Entry.Action}|{call.Entry.Outcome}|{call.Entry.DurationMs}")),
        DashboardCards.Commands => string.Join(';', _commandsRecent.Select(run => $"{run.Id}|{CommandState(run).Text}")),
        DashboardCards.NuGet => NuGetToken(),
        DashboardCards.Workspaces => string.Join(';', _workspaces.Select(workspace => $"{workspace.Id}|{workspace.Name}|{workspace.Enabled}|{workspace.LastIndexedUtc}")),
        _ => string.Empty
    };

    private string NuGetToken() => _nuget is not { } stats
        ? "-"
        : $"{stats.Ready}|{stats.Building}|{stats.Failed}|{stats.Types}|{stats.Symbols}|{stats.IndexBytes}|{stats.CachedResults}|"
          + string.Join(';', _nugetRecent.Select(entry => $"{entry.Package.Key.PackageId}|{entry.Package.Key.Version}|{entry.Package.State}"));

    private async ValueTask OnProgressAsync(CodeIndexProgress progress)
    {
        _progress[progress.WorkspaceId] = progress;
        try
        {
            await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException)
        {
            // The circuit ended while the event was in flight.
        }
    }

    private void OnStatusChanged()
    {
        try
        {
            _ = InvokeAsync(StateHasChanged);
            _ = RefreshCallStatsAsync();
        }
        catch (ObjectDisposedException)
        {
            // The circuit ended while the change was in flight.
        }
    }

    // When a tool call ends its audit row already exists, so the token totals and the recent calls are re-read at once instead of
    // at the next reload. A call that ends while a read is running makes it read once more.
    private async Task RefreshCallStatsAsync()
    {
        if (Interlocked.Exchange(ref _callStatsRunning, 1) == 1)
        {
            Volatile.Write(ref _callStatsAgain, 1);
            return;
        }

        try
        {
            do
            {
                Volatile.Write(ref _callStatsAgain, 0);
                var usage = await ContextUsage.GetAsync(Cancel);
                var recent = await Audit.QueryAsync(new AuditQuery(AuditCategory.Tool, null, null, RecentCallRows), Cancel);
                await InvokeAsync(() =>
                {
                    _tokens = usage;
                    _recent = recent;
                    StateHasChanged();
                });
            }
            while (Volatile.Read(ref _callStatsAgain) == 1);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (ObjectDisposedException)
        {
            // The circuit ended.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The token totals on the Home page could not be refreshed");
        }
        finally
        {
            Volatile.Write(ref _callStatsRunning, 0);
        }
    }

    private static string CallsText(int calls) => calls == 1 ? "1 call" : $"{DisplayFormat.Count(calls)} calls";

    // Redraws every second so the pairing countdown is live, and reloads the data every few seconds for the call table.
    private async Task TickAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var ticks = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(Cancel))
            {
                ticks++;
                if (ticks % ReloadEveryTicks == 0)
                {
                    await InvokeAsync(async () =>
                    {
                        await LoadAsync();
                        StateHasChanged();
                    });
                }
                else if (First is not null || _commandsWaiting.Count > 0)
                {
                    await InvokeAsync(StateHasChanged);
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

    public override void Dispose()
    {
        Status.Changed -= OnStatusChanged;
        _progressSubscription?.Dispose();
        base.Dispose();
    }
}
