using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Waymark.Host.Ui;

namespace Waymark.Host.Components.Shared;

/// <summary>
/// The call-details side panel, drawn once by the layout. It shows the call that <see cref="CallPanelState"/> holds, so a call opens in place
/// from the Audit list, the Dashboard and the Live log, and the page behind it keeps its filters, scroll position and live feed.
/// </summary>
public partial class CallPanel : ComponentBase, IDisposable
{
    private ElementReference _panel;
    private bool _focusPending;
    private bool _wasOpen;

    [Inject]
    private CallPanelState State { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Inject]
    private IJSRuntime Js { get; set; } = default!;

    [Inject]
    private ILogger<CallPanel> Logger { get; set; } = default!;

    protected override void OnInitialized()
    {
        State.Changed += OnStateChanged;
        Navigation.LocationChanged += OnLocationChanged;
        _wasOpen = State.OpenId is not null;
    }

    // Keyboard users who opened a call land in the panel; stepping to another call keeps focus on the buttons they pressed.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_focusPending)
        {
            return;
        }

        _focusPending = false;
        try
        {
            await _panel.FocusAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            Logger.LogDebug(ex, "The call panel could not take focus");
        }
    }

    private void OnKey(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            State.Close();
        }
    }

    private void OnStateChanged()
    {
        try
        {
            _ = InvokeAsync(() =>
            {
                var open = State.OpenId is not null;
                _focusPending = open && !_wasOpen;
                _wasOpen = open;
                StateHasChanged();
            });
        }
        catch (ObjectDisposedException)
        {
            // The circuit ended while the change was in flight.
        }
    }

    // The full page shows the same call, so the panel steps aside when the owner goes there.
    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        if (new Uri(e.Location).AbsolutePath.StartsWith("/calls/", StringComparison.Ordinal))
        {
            State.Close();
        }
    }

    public void Dispose()
    {
        State.Changed -= OnStateChanged;
        Navigation.LocationChanged -= OnLocationChanged;
    }
}
