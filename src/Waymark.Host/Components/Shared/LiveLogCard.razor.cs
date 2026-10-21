using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Waymark.Logging;

namespace Waymark.Host.Components.Shared;

/// <summary>
/// A small live window for the Home page: the latest log lines as they arrive, and a link to the full Live page.
/// It keeps its own short buffer and its own feed from the shared hub, and lets go of both when the page closes.
/// </summary>
public partial class LiveLogCard : LiveComponentBase
{
    private const int KeptLines = 60;
    private const string TerminalId = "wm-home-live";
    private static readonly TimeSpan RenderEvery = TimeSpan.FromMilliseconds(500);

    private readonly List<LogEntry> _entries = [];
    private List<LogEntry> _rows = [];
    private LiveLogSubscription? _subscription;
    private volatile bool _dirty;
    private long _version;

    [Inject]
    private LiveLogHub Hub { get; set; } = default!;

    [Inject]
    private IJSRuntime Js { get; set; } = default!;

    [Inject]
    private ILogger<LiveLogCard> Logger { get; set; } = default!;

    protected override void OnInitialized()
    {
        _subscription = Hub.Subscribe(KeptLines, out var snapshot);
        _entries.AddRange(snapshot);
        CopyRows();
        _ = PumpAsync(_subscription);
        _ = RenderLoopAsync();
    }

    // Follows new lines while the reader is at the bottom, and stays put once they scroll up to read.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            await Js.InvokeVoidAsync("wmConsole.follow", Cancel, TerminalId, firstRender);
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
                lock (_entries)
                {
                    _entries.Add(entry);
                    if (_entries.Count > KeptLines)
                    {
                        _entries.RemoveRange(0, _entries.Count - KeptLines);
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
            Logger.LogError(ex, "The live log card's feed stopped");
        }
    }

    // Draws at most twice a second, and only when a line arrived.
    private async Task RenderLoopAsync()
    {
        using var timer = new PeriodicTimer(RenderEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(Cancel))
            {
                if (!_dirty)
                {
                    continue;
                }

                _dirty = false;
                await InvokeAsync(() =>
                {
                    _version++;
                    CopyRows();
                    StateHasChanged();
                });
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

    private void CopyRows()
    {
        lock (_entries)
        {
            _rows = [.. _entries];
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }
}
