using Microsoft.AspNetCore.Components;

namespace Waymark.Host.Components.Shared;

/// <summary>
/// A small pulsing dot for a card header. It lights up when <see cref="Token"/> changes and goes out a few seconds after
/// the last change, so the owner can tell "this just changed" from "this was reloaded and is the same".
/// It redraws only itself, so the card around it is not redrawn when the dot goes out.
/// </summary>
public sealed partial class ActivityDot : ComponentBase, IDisposable
{
    private const int MinHoldSeconds = 1;
    private const int MaxHoldSeconds = 60;

    private bool _hasBaseline;
    private string? _lastToken;
    private bool _active;
    private CancellationTokenSource? _hold;

    /// <summary>Anything that changes whenever the card's content changes. The first value only sets the baseline.</summary>
    [Parameter]
    public string? Token { get; set; }

    /// <summary>How long the dot stays lit after the last change.</summary>
    [Parameter]
    public int HoldSeconds { get; set; } = 4;

    protected override void OnParametersSet()
    {
        if (!_hasBaseline)
        {
            _hasBaseline = true;
            _lastToken = Token;
            return;
        }

        if (string.Equals(Token, _lastToken, StringComparison.Ordinal))
        {
            return;
        }

        _lastToken = Token;
        _active = true;
        RestartHold();
    }

    private void RestartHold()
    {
        _hold?.Cancel();
        _hold?.Dispose();
        var hold = new CancellationTokenSource();
        _hold = hold;
        _ = DimAfterAsync(TimeSpan.FromSeconds(Math.Clamp(HoldSeconds, MinHoldSeconds, MaxHoldSeconds)), hold.Token);
    }

    private async Task DimAfterAsync(TimeSpan delay, CancellationToken cancel)
    {
        try
        {
            await Task.Delay(delay, cancel);
            await InvokeAsync(() =>
            {
                // A newer change restarted the hold; leave the dot lit for that one.
                if (cancel.IsCancellationRequested)
                {
                    return;
                }

                _active = false;
                StateHasChanged();
            });
        }
        catch (OperationCanceledException)
        {
            // A newer change, or the page closed.
        }
        catch (ObjectDisposedException)
        {
            // The circuit ended while the dot was waiting.
        }
    }

    public void Dispose()
    {
        _hold?.Cancel();
        _hold?.Dispose();
        _hold = null;
    }
}
