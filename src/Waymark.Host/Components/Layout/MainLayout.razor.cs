using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Waymark.Core.Edge;
using Waymark.Host.Components.Shared;
using Waymark.Host.Components.Theme;
using Waymark.Host.Ui;

namespace Waymark.Host.Components.Layout;

public partial class MainLayout : LayoutComponentBase, IDisposable
{
    private const string ThemeKey = "wm-theme";
    private const string NavKey = "wm-nav";
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(10);

    private readonly MudTheme _theme = WaymarkTheme.Create();
    private readonly CancellationTokenSource _cancel = new();
    private MudThemeProvider? _themeProvider;
    private bool _dark;
    private bool _busy;
    private bool _navCollapsed = true;
    private bool _navAnimate;
    private PageErrorBoundary? _boundary;

    [Inject]
    private ShellStatus Status { get; set; } = default!;

    [Inject]
    private IJSRuntime Js { get; set; } = default!;

    [Inject]
    private ILogger<MainLayout> Logger { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    private (StatusState State, string Text) ServerChip =>
        Status.Paused ? (StatusState.Attention, "Paused") : (StatusState.Healthy, "Server running");

    private (StatusState State, string Text) TunnelChip => Status.Tunnel.Phase switch
    {
        TunnelPhase.Online => (StatusState.Healthy, "Tunnel online"),
        TunnelPhase.Installing or TunnelPhase.Starting => (StatusState.Working, "Tunnel starting"),
        TunnelPhase.Failed => (StatusState.Failed, "Tunnel failed"),
        _ => Status.PublicUrl is null ? (StatusState.Off, "No public address") : (StatusState.Off, "Tunnel off")
    };

    private string ApprovalText => Status.PendingApprovals == 1 ? "1 approval waiting" : $"{Status.PendingApprovals} approvals waiting";

    private string CommandText => Status.PendingCommands == 1 ? "1 command waiting" : $"{Status.PendingCommands} commands waiting";

    private string ActiveText => Status.ActiveNow == 1 ? "1 running" : $"{Status.ActiveNow} running";

    private string ShellClass => (_navCollapsed ? " nav-collapsed" : string.Empty) + (_navAnimate ? " nav-anim" : string.Empty);

    // A collapsed menu shows only icons, so each item gets a tooltip with its name.
    private string? Tip(string label) => _navCollapsed ? label : null;

    protected override async Task OnInitializedAsync()
    {
        await Status.RefreshAsync(_cancel.Token);
        Status.Changed += OnStatusChanged;
        Navigation.LocationChanged += OnLocationChanged;
        _ = PollAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        try
        {
            _navCollapsed = await Js.InvokeAsync<string?>("localStorage.getItem", NavKey) != "expanded";
            var saved = await Js.InvokeAsync<string?>("localStorage.getItem", ThemeKey);
            _dark = saved switch
            {
                "dark" => true,
                "light" => false,
                _ => _themeProvider is not null && await _themeProvider.GetSystemDarkModeAsync()
            };
            StateHasChanged();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
            Logger.LogDebug(ex, "The saved theme and menu width could not be read; the defaults are used");
        }
    }

    private async Task ToggleThemeAsync()
    {
        _dark = !_dark;
        try
        {
            await Js.InvokeVoidAsync("localStorage.setItem", ThemeKey, _dark ? "dark" : "light");
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            Logger.LogDebug(ex, "The theme choice could not be saved");
        }
    }

    // The menu keeps its width choice per browser, like the theme. The slide animation starts only after the first click, so restoring the saved choice never animates.
    private async Task ToggleNavAsync()
    {
        _navAnimate = true;
        _navCollapsed = !_navCollapsed;
        try
        {
            await Js.InvokeVoidAsync("localStorage.setItem", NavKey, _navCollapsed ? "collapsed" : "expanded");
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            Logger.LogDebug(ex, "The menu width choice could not be saved");
        }
    }

    private async Task TogglePauseAsync()
    {
        _busy = true;
        try
        {
            await Status.SetPausedAsync(!Status.Paused, _cancel.Token);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(RefreshEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(_cancel.Token))
            {
                await Status.RefreshAsync(_cancel.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The circuit ended.
        }
    }

    private void OnStatusChanged()
    {
        try
        {
            _ = InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException)
        {
            // The circuit ended while the change was in flight.
        }
    }

    // A page that failed to draw is replaced by a notice; going to another page starts clean.
    private void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e) =>
        _ = InvokeAsync(RecoverPage);

    private void RecoverPage() => _boundary?.Recover();

    public void Dispose()
    {
        Navigation.LocationChanged -= OnLocationChanged;
        Status.Changed -= OnStatusChanged;
        _cancel.Cancel();
        _cancel.Dispose();
        GC.SuppressFinalize(this);
    }
}
