using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Waymark.Core.Audit;
using Waymark.Host.Components.Shared;
using Waymark.Host.Mcp;

namespace Waymark.Host.Components.Pages;

/// <summary>
/// The page of one audit entry. For a tool call it shows the input straight away and the output only when the owner asks for it: the output can be long
/// (up to the size limit) and most visits only need to see what was asked.
/// </summary>
public partial class CallDetails : LiveComponentBase
{
    private const int PreviewChars = 4000;

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly List<ArgumentView> _arguments = [];
    private StoredAuditEntry? _entry;
    private StoredToolPayload? _payload;
    private PayloadSettings? _settings;
    private string? _output;
    private string? _error;
    private string? _copied;
    private bool _loading = true;
    private bool _loadingOutput;

    [Parameter]
    public long Id { get; set; }

    [Inject]
    private IAuditStore Store { get; set; } = default!;

    [Inject]
    private ToolPayloadService Payloads { get; set; } = default!;

    [Inject]
    private IJSRuntime Js { get; set; } = default!;

    [Inject]
    private ILogger<CallDetails> Logger { get; set; } = default!;

    private static int MaxOutputKept => ToolPayloadLimits.MaxOutputChars;

    private sealed record ArgumentView(string Name, string Kind, string Text)
    {
        public bool IsLong => Text.Length > PreviewChars;

        public string Preview => IsLong ? Text[..PreviewChars] + "\n..." : Text;
    }

    protected override async Task OnParametersSetAsync()
    {
        _loading = true;
        _entry = null;
        _payload = null;
        _output = null;
        _copied = null;
        _error = null;
        _arguments.Clear();
        _expanded.Clear();
        try
        {
            _entry = await Store.GetAsync(Id, Cancel);
            if (_entry is { Entry.Category: AuditCategory.Tool })
            {
                _payload = await Store.GetPayloadAsync(Id, Cancel);
                _settings = await Payloads.GetSettingsAsync(Cancel);
                if (_payload is not null)
                {
                    BuildArguments(_payload);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The call {Id} could not be loaded", Id);
            _error = "The call could not be loaded.";
        }
        finally
        {
            _loading = false;
        }
    }

    // Each argument is shown on its own, text as text (line breaks kept), so a script or a file body can be read and not just parsed out of an escaped JSON line.
    // A cut input is not valid JSON any more, so it is shown as it is.
    private void BuildArguments(StoredToolPayload payload)
    {
        if (payload.InputCut)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(payload.Input);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = property.Value;
                _arguments.Add(value.ValueKind == JsonValueKind.String
                    ? new ArgumentView(property.Name, "text", value.GetString() ?? string.Empty)
                    : new ArgumentView(property.Name, value.ValueKind.ToString().ToLowerInvariant(), JsonSerializer.Serialize(value, Pretty)));
            }
        }
        catch (JsonException)
        {
            _arguments.Clear();
        }
    }

    private bool IsExpanded(ArgumentView argument) => _expanded.Contains(argument.Name);

    private void ToggleArgument(string name)
    {
        if (!_expanded.Remove(name))
        {
            _expanded.Add(name);
        }
    }

    private async Task ShowOutputAsync()
    {
        if (_loadingOutput)
        {
            return;
        }

        _loadingOutput = true;
        try
        {
            _output = await Store.GetPayloadOutputAsync(Id, Cancel) ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The output of call {Id} could not be loaded", Id);
            _error = "The output could not be loaded.";
        }
        finally
        {
            _loadingOutput = false;
        }
    }

    private void HideOutput()
    {
        _output = null;
        _copied = null;
    }

    private Task CopyInputAsync() => _payload is null ? Task.CompletedTask : CopyAsync(_payload.Input, "input");

    private Task CopyOutputAsync() => _output is null ? Task.CompletedTask : CopyAsync(_output, "output");
    private async Task CopyAsync(string text, string what)
    {
        try
        {
            await Js.InvokeVoidAsync("waymark.copy", Cancel, text);
            _copied = what;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException)
        {
            Logger.LogDebug(ex, "The copy was not delivered");
        }
    }
}
