using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using Waymark.Core.Commands;

namespace Waymark.Host.Components.Pages;

// The reader's tools of the Console page: search in the output, jump to the next error, wrap, the exit banner and the run filter.
// They only work on what is shown and never change a run.
public partial class CommandConsole
{
    private const string TextMode = "text";
    private const string ErrorMode = "error";

    private string _query = string.Empty;
    private string _findInfo = string.Empty;
    private string _findMode = TextMode;
    private string _runFilter = string.Empty;
    private bool _wrap = true;

    private async Task OnQueryChangedAsync()
    {
        if (_query.Trim().Length == 0)
        {
            await ClearSearchAsync();
            return;
        }

        await FindAsync(TextMode, 1, reset: true);
    }

    private Task StepAsync(bool forward) => FindAsync(_findMode, forward ? 1 : -1, reset: _findInfo.Length == 0);

    private Task NextErrorAsync() => FindAsync(ErrorMode, 1, reset: _findMode != ErrorMode || _findInfo.Length == 0);

    private async Task FindAsync(string mode, int step, bool reset)
    {
        var query = mode == TextMode ? _query.Trim() : string.Empty;
        if (mode == TextMode && query.Length == 0)
        {
            return;
        }

        try
        {
            var result = await Js.InvokeAsync<FindResult>("wmConsole.find", Cancel, TerminalId, query, mode, step, reset);
            _findMode = mode;
            var what = mode == ErrorMode ? "Error " : string.Empty;
            _findInfo = result.Count == 0
                ? (mode == ErrorMode ? "No errors found" : "No matches")
                : $"{what}{result.Index + 1} of {result.Count}";
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            Logger.LogDebug(ex, "The console search could not run");
        }
    }

    // New output arrived while a search is active: mark its matching lines too, without moving the reader.
    private async Task MarkAsync()
    {
        try
        {
            await Js.InvokeAsync<FindResult>("wmConsole.find", Cancel, TerminalId, _findMode == TextMode ? _query.Trim() : string.Empty, _findMode, 0, false);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            Logger.LogDebug(ex, "The console marks could not be refreshed");
        }
    }

    private async Task ClearSearchAsync()
    {
        _query = string.Empty;
        _findInfo = string.Empty;
        _findMode = TextMode;
        try
        {
            await Js.InvokeVoidAsync("wmConsole.clearFind", Cancel, TerminalId);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            Logger.LogDebug(ex, "The console marks could not be cleared");
        }
    }

    private async Task OnSearchKeyAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            await StepAsync(!e.ShiftKey);
        }
        else if (e.Key == "Escape")
        {
            await ClearSearchAsync();
        }
    }

    private async Task ToggleWrapAsync()
    {
        _wrap = !_wrap;
        try
        {
            await Js.InvokeAsync<string?>("wmConsole.pref", Cancel, "wrap", _wrap ? "on" : "off");
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            Logger.LogDebug(ex, "The wrap choice could not be saved");
        }
    }

    private async Task LoadWrapPreferenceAsync()
    {
        try
        {
            var saved = await Js.InvokeAsync<string?>("wmConsole.pref", Cancel, "wrap", null);
            if (saved == "off")
            {
                _wrap = false;
                StateHasChanged();
            }
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            Logger.LogDebug(ex, "The wrap choice could not be read");
        }
    }

    private async Task CopyCommandAsync()
    {
        if (_selected is not { } run)
        {
            return;
        }

        try
        {
            await Js.InvokeVoidAsync("navigator.clipboard.writeText", Cancel, run.CommandLine);
            Snackbar.Add("Command copied.", Severity.Success);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            Snackbar.Add("Copying is blocked by the browser. Select the text and copy it by hand.", Severity.Warning);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
    }

    // One plain sentence about how a finished run ended. Null while it is waiting or running.
    private string? BannerText(CommandRun run)
    {
        var exit = run.ExitCode is { } code ? $" (exit code {code})" : string.Empty;
        return run.Status switch
        {
            CommandRunStatus.Succeeded => $"Finished OK in {Elapsed(run)}{exit}.",
            CommandRunStatus.Failed => $"Failed after {Elapsed(run)}{exit}. Use Next error to find the cause.",
            CommandRunStatus.TimedOut => $"Stopped at the time limit after {Elapsed(run)}.",
            CommandRunStatus.Denied => "Not run: it was denied.",
            CommandRunStatus.Cancelled => "Stopped before it finished.",
            _ => null
        };
    }

    private static string BannerClass(CommandRun run) => run.Status switch
    {
        CommandRunStatus.Succeeded => "ok",
        CommandRunStatus.Failed or CommandRunStatus.TimedOut => "err",
        _ => "off"
    };

    private static bool RunMatches(CommandRun run, string filter) =>
        Label(run).Contains(filter, StringComparison.OrdinalIgnoreCase)
        || run.CommandLine.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Chip(run).Text.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>What the browser reports after a search: how many lines matched and which one is current (-1 for none).</summary>
    public sealed record FindResult(int Count, int Index);
}
