using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Waymark.Core.LiveFeed;
using Waymark.Host.Components.Shared;
using Waymark.Host.Ui;
using Waymark.Logging;

namespace Waymark.Host.Components.Pages;

public partial class Live : LiveComponentBase
{
    private const int MaxKept = 200;
    private const string AllTab = "all";
    private const string ErrorsTab = "errors";
    private const string TerminalId = "wm-live-term";
    private static readonly TimeSpan RenderEvery = TimeSpan.FromMilliseconds(500);

    private static readonly LiveTab[] Tabs =
    [
        new(AllTab, "All"),
        new("tools", "Tools"),
        new("watcher", "Watcher"),
        new("auth", "Auth"),
        new("commands", "Commands"),
        new(ErrorsTab, "Errors"),
    ];

    private readonly List<LogEntry> _entries = [];
    private readonly HashSet<long> _open = [];
    private IReadOnlyList<ActivityItem> _running = [];
    private List<LogEntry> _rows = [];
    private LiveLogSubscription? _subscription;
    private string _tab = AllTab;
    private string _text = string.Empty;
    private int _problemCount;
    private bool _paused;
    private bool _forceFollow;
    private volatile bool _dirty;

    [Inject]
    private LiveLogHub Hub { get; set; } = default!;

    [Inject]
    private LiveActivity Running { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private IJSRuntime Js { get; set; } = default!;

    [Inject]
    private ILogger<Live> Logger { get; set; } = default!;

    private long Dropped => _subscription?.Dropped ?? 0;

    protected override void OnInitialized()
    {
        _subscription = Hub.Subscribe(MaxKept, out var snapshot);
        _entries.AddRange(snapshot);
        Refresh();
        Running.Changed += OnRunningChanged;
        _ = PumpAsync(_subscription);
        _ = RenderLoopAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var force = firstRender || _forceFollow;
        _forceFollow = false;
        try
        {
            await Js.InvokeVoidAsync("wmConsole.follow", Cancel, TerminalId, force);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            // The circuit ended or the page is not interactive yet; the next draw follows again.
        }
    }

    private async Task PumpAsync(LiveLogSubscription subscription)
    {
        try
        {
            await foreach (var entry in subscription.Reader.ReadAllAsync(Cancel))
            {
                if (_paused)
                {
                    continue;
                }

                lock (_entries)
                {
                    _entries.Add(entry);
                    if (_entries.Count > MaxKept)
                    {
                        _entries.RemoveRange(0, _entries.Count - MaxKept);
                    }
                }

                _dirty = true;
            }
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The live log feed stopped");
        }
    }

    // Draws again when a line arrived, and every tick while something runs so its time keeps counting.
    private async Task RenderLoopAsync()
    {
        using var timer = new PeriodicTimer(RenderEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(Cancel))
            {
                if (_dirty || Running.Count > 0)
                {
                    _dirty = false;
                    await InvokeAsync(() =>
                    {
                        Refresh();
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

    private void OnRunningChanged() => _dirty = true;

    private void Refresh()
    {
        _running = Running.Current();
        var text = _text.Trim();
        lock (_entries)
        {
            _problemCount = _entries.Count(entry => entry.Level >= LogLevel.Warning);
            _rows = _entries
                .Where(Matches)
                .Where(entry => text.Length == 0 || entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private bool Matches(LogEntry entry) => _tab switch
    {
        "tools" => entry.Kind == LogKind.Tool,
        "watcher" => entry.Kind == LogKind.Watcher,
        "auth" => entry.Kind == LogKind.Auth,
        "commands" => entry.Kind == LogKind.Command,
        ErrorsTab => entry.Level >= LogLevel.Warning,
        _ => true
    };

    private void SelectTab(string id)
    {
        _tab = id;
        _forceFollow = true;
        _open.Clear();
        Refresh();
    }

    private void Clear()
    {
        Hub.Clear();
        lock (_entries)
        {
            _entries.Clear();
        }

        _open.Clear();
        Refresh();
    }

    private void Toggle(long id)
    {
        if (!_open.Remove(id))
        {
            _open.Add(id);
        }
    }

    private string Elapsed(ActivityItem item)
    {
        var seconds = Math.Max(0, (Time.GetUtcNow() - item.StartedUtc).TotalSeconds);
        return string.Create(CultureInfo.InvariantCulture, $"{seconds:0.0}s");
    }

    private static bool HasDetails(LogEntry entry) => !string.IsNullOrEmpty(entry.Exception);

    private static string KindWord(LogKind kind) => LiveLineFormat.KindWord(kind);

    private static string LevelClass(LogLevel level) => LiveLineFormat.LevelClass(level);

    private static string LevelWord(LogLevel level) => LiveLineFormat.LevelWord(level);

    public override void Dispose()
    {
        Running.Changed -= OnRunningChanged;
        _subscription?.Dispose();
        base.Dispose();
    }

    private sealed record LiveTab(string Id, string Name);
}
