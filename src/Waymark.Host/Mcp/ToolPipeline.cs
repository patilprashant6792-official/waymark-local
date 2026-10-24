using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Waymark.Core.Audit;
using Waymark.Core.Guides;
using Waymark.Core.LiveFeed;
using Waymark.Core.Security;
using Waymark.Core.Settings;
using Waymark.Core.Tools;
using Waymark.Host.Auth;

namespace Waymark.Host.Mcp;

/// <summary>
/// Wraps every tool call: refuses it while Waymark is paused or the tool is switched off, applies the time and
/// output limits, then writes one audit row and one live-log line. The list filter hides switched-off tools.
/// </summary>
public sealed class ToolPipeline(
    IToolRegistry registry,
    IToolSettingsStore settings,
    IConfigStore config,
    IAuditStore audit,
    TimeProvider time,
    ToolPipelineOptions options,
    ToolResultGuard guard,
    LiveActivity activity,
    IContextMeter meter,
    ToolGuideDelivery guideDelivery,
    ILogger<ToolPipeline> logger)
{
    private const string UnknownClient = "unknown";
    private const string WorkspaceArgument = "workspace";
    private const string AlwaysLoadKey = "anthropic/alwaysLoad";
    private const int MaxTargetChars = 128;

    public async ValueTask<ListToolsResult> ListAsync(
        RequestContext<ListToolsRequestParams> context,
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> next,
        CancellationToken ct)
    {
        var result = await next(context, ct);
        var rows = await settings.ListAsync(ct);
        result.Tools = result.Tools.Where(tool => !registry.TryGet(tool.Name, out var d) || IsEnabled(d, rows)).ToList();
        foreach (var tool in result.Tools)
        {
            if (!registry.TryGet(tool.Name, out var descriptor))
            {
                continue;
            }

            if (string.Equals(tool.Name, GuideRules.OverviewTool, StringComparison.Ordinal))
            {
                // The way in: listed upfront, so a client that defers tool definitions still sees it before it has used any other Waymark tool.
                tool.Meta ??= new JsonObject();
                tool.Meta[AlwaysLoadKey] = true;
            }
            else if (guideDelivery.IsGuided(descriptor.Module))
            {
                tool.InputSchema = GuideSchema.WithGuideProperty(tool.InputSchema, descriptor.Module);
            }
        }
        return result;
    }

    public async ValueTask<CallToolResult> CallAsync(
        RequestContext<CallToolRequestParams> context,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        CancellationToken ct)
    {
        var tool = context.Params?.Name ?? UnknownClient;
        var client = context.User?.FindFirst(BearerAuthHandler.ClientClaim)?.Value ?? UnknownClient;
        var started = Stopwatch.GetTimestamp();
        var purpose = ToolPurpose.From(context.Params?.Arguments);
        using var running = activity.Begin(LogKind.Tool, purpose is null ? tool : $"{tool}  why: {purpose}");
        var outcome = AuditOutcome.Ok;
        string? error = null;
        var bytes = 0;
        var tokens = 0;
        CallToolResult result;

        // Every tool of a module with a guide accepts guide: true. The flag is removed here, so the tool never sees it.
        // The overview tool needs no flag: calling it is the request.
        var guideModule = GuideModuleOf(tool);
        var guideRequested = guideModule is not null
            && (ToolGuideDelivery.TakeFlag(context.Params?.Arguments) || string.Equals(tool, GuideRules.OverviewTool, StringComparison.Ordinal));
        var refused = false;

        try
        {
            var refusal = await RefusalReasonAsync(tool, context.Params?.Arguments, ct);
            if (refusal is not null)
            {
                refused = true;
                outcome = AuditOutcome.Denied;
                error = refusal;
                result = Failure(refusal);
            }
            else
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(options.CallTimeout);
                result = await next(context, limit.Token);
                if (result.IsError == true)
                {
                    outcome = AuditOutcome.Error;
                    error = "The tool reported an error.";
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            outcome = AuditOutcome.Error;
            error = $"Timed out after {options.CallTimeout.TotalSeconds:0} seconds.";
            result = Failure(error);
        }
        catch (OperationCanceledException)
        {
            await RecordAsync(context, tool, client, started, AuditOutcome.Error, 0, 0, "Cancelled by the client.");
            throw;
        }
        catch (ModelContextProtocol.McpException ex)
        {
            // The tool says in plain words why it refused or failed; the client gets that message as it is.
            outcome = AuditOutcome.Error;
            error = ex.Message;
            result = Failure(error);
        }
        catch (Exception ex)
        {
            var reference = Guid.NewGuid().ToString("N")[..8];
            logger.LogError(ex, "Tool {Tool} failed unexpectedly (reference {Reference})", tool, reference);
            outcome = AuditOutcome.Error;
            error = $"Unexpected failure, reference {reference}.";
            result = Failure(error);
        }

        // Every result, error messages included, passes the guard: secrets are hidden, one safety line is added, and what was hidden is recorded.
        // If the check itself fails, the output is withheld rather than sent unchecked.
        try
        {
            var findings = await guard.RedactAsync(result, ct);
            bytes = Bound(result) + guard.AppendNotice(result, findings);
            tokens = CountTokens(result);
            await guard.RecordAsync(findings, tool, client, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Tool {Tool}: the output could not be checked for secrets and was withheld", tool);
            outcome = AuditOutcome.Error;
            error = "The output could not be checked for secrets, so it was withheld.";
            result = Failure(error);
            bytes = 0;
            tokens = 0;
        }

        // The guide is Waymark's own text and goes in after the secret check and the size limit, so neither can cut or alter it.
        // A refused call gets neither guide nor notice.
        if (!refused && guideModule is not null)
        {
            var added = await guideDelivery.AttachAsync(result, guideModule, guideRequested, client, ct);
            bytes += added.Bytes;
            tokens += added.Tokens;
        }

        await RecordAsync(context, tool, client, started, outcome, bytes, tokens, error);
        return result;
    }

    // The module whose guide covers this tool; null for tools without one (Waymark's own).
    private string? GuideModuleOf(string tool) =>
        registry.TryGet(tool, out var descriptor) && guideDelivery.IsGuided(descriptor.Module) ? descriptor.Module : null;

    private async Task<string?> RefusalReasonAsync(string tool, IDictionary<string, JsonElement>? arguments, CancellationToken ct)
    {
        if (await IsPausedAsync(ct))
        {
            return "Waymark is paused. Resume it in the admin UI to use tools.";
        }

        if (!registry.TryGet(tool, out var descriptor))
        {
            return null;
        }

        var rows = await settings.ListAsync(ct);
        if (!IsEnabled(descriptor, rows))
        {
            return $"The tool '{tool}' is switched off in Waymark.";
        }

        // Waymark hides secrets in what it returns. A marker inside something about to be written or run would put the marker, not the secret, into the file.
        return descriptor.Gate == ToolGate.Confirm && CarriesRedactionMarker(arguments)
            ? "The input contains a [redacted:...] marker. Waymark hides secrets in what it returns; never write a marker into a file or command. Refer to a vault secret by name, or ask the user for the real value."
            : null;
    }

    private static bool CarriesRedactionMarker(IDictionary<string, JsonElement>? arguments) =>
        arguments is not null
        && arguments.Values.Any(value => SensitiveDataScanner.ContainsMarker(value.GetRawText()));

    // A value that cannot be read counts as paused: the switch fails closed.
    private async Task<bool> IsPausedAsync(CancellationToken ct)
    {
        var setting = await config.GetAsync(SettingScope.Global, SettingKeys.Paused, ct);
        if (setting is null)
        {
            return false;
        }

        try
        {
            return JsonSerializer.Deserialize<bool>(setting.ValueJson);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    // A tool with no saved row follows ToolDefaults: Read tools are on, Confirm tools are off.
    private static bool IsEnabled(ToolDescriptor descriptor, IReadOnlyList<ToolSetting> rows)
    {
        var row = rows.FirstOrDefault(r => string.Equals(r.ToolName, descriptor.Name, StringComparison.OrdinalIgnoreCase));
        return row?.Enabled ?? ToolDefaults.IsOnByDefault(descriptor);
    }

    // Cuts text beyond the output limit and returns the size of what is sent.
    private int Bound(CallToolResult result)
    {
        var remaining = options.MaxOutputChars;
        var bytes = 0;
        foreach (var block in result.Content.OfType<TextContentBlock>())
        {
            var text = block.Text ?? string.Empty;
            if (text.Length > remaining)
            {
                var cut = Math.Max(remaining, 0);
                if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
                {
                    cut--;
                }

                block.Text = text[..cut] + $"\n[Output cut at {options.MaxOutputChars} characters. Narrow the request to see the rest.]";
                remaining = 0;
            }
            else
            {
                remaining -= text.Length;
            }

            bytes += Encoding.UTF8.GetByteCount(block.Text ?? string.Empty);
        }

        return bytes;
    }

    // The tokens the AI will read: every text block as it is finally sent, the safety line included.
    private int CountTokens(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Sum(block => meter.CountTokens(block.Text));

    private async Task RecordAsync(
        RequestContext<CallToolRequestParams> context,
        string tool,
        string client,
        long started,
        AuditOutcome outcome,
        int bytes,
        int tokens,
        string? error)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);
        var arguments = context.Params?.Arguments;
        var argumentNames = arguments is null ? null : JsonSerializer.Serialize(arguments.Keys.Order(StringComparer.Ordinal));
        var purpose = ToolPurpose.From(arguments);
        string? target = null;
        if (arguments is not null && arguments.TryGetValue(WorkspaceArgument, out var workspace) && workspace.ValueKind == JsonValueKind.String)
        {
            var name = workspace.GetString();
            target = name is { Length: > MaxTargetChars } ? name[..MaxTargetChars] : name;
        }

        logger.LogToolCall(
            outcome switch
            {
                AuditOutcome.Ok => LogLevel.Information,
                AuditOutcome.Denied => LogLevel.Warning,
                _ => LogLevel.Error
            },
            tool,
            target,
            error is null ? "ok" : outcome.ToString().ToLowerInvariant() + ": " + error,
            (long)elapsed.TotalMilliseconds,
            tokens,
            ToolArgumentSummary.Build(arguments),
            purpose);

        try
        {
            await audit.AppendAsync(
                new AuditEntry(time.GetUtcNow(), AuditCategory.Tool, client, tool, target, argumentNames, (int)elapsed.TotalMilliseconds, bytes, outcome, error, tokens, purpose),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not write the audit row for tool {Tool}", tool);
        }
    }

    private static CallToolResult Failure(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
