using Microsoft.Extensions.Logging;

namespace Waymark.Core.LiveFeed;

/// <summary>
/// The four things the Live page shows, each with one fixed shape: a tool call, a file change, a sign-in event and a command.
/// Anything logged some other way stays out of the live view unless it is an error.
/// </summary>
public static class ActivityLogging
{
    private static readonly EventId ToolEvent = new(1001, LogEvents.ToolPrefix + "call");
    private static readonly EventId WatchEvent = new(1002, LogEvents.WatcherPrefix + "event");
    private static readonly EventId AuthEvent = new(1003, LogEvents.AuthPrefix + "event");
    private static readonly EventId AuthRequestEvent = new(1004, LogEvents.AuthPrefix + "request");
    private static readonly EventId CommandEvent = new(1005, LogEvents.CommandPrefix + "run");

    /// <summary>
    /// One call Claude made to a tool. <paramref name="arguments"/> is a short, already cut summary of what it was asked.
    /// <paramref name="tokens"/> is how many tokens of the AI's context what the tool sent back takes up.
    /// </summary>
    public static void LogToolCall(this ILogger logger, LogLevel level, string tool, string? workspace, string outcome, long elapsedMs, long tokens, string arguments)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Log(level, ToolEvent, "{Tool}  {Workspace}  {Outcome}  {ElapsedMs} ms  {Tokens} tokens  {Arguments}", tool, workspace ?? "-", outcome, elapsedMs, tokens, arguments);
    }

    /// <summary>A file or workspace change seen by the watcher, or a problem with watching.</summary>
    public static void LogWatch(this ILogger logger, LogLevel level, string action, string workspace, string detail, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Log(level, WatchEvent, exception, "{Action}  {Workspace}  {Detail}", action, workspace, detail);
    }

    /// <summary>A pairing, approval, sign-in or revocation.</summary>
    public static void LogAuth(this ILogger logger, LogLevel level, string action, string actor, string target, string outcome)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Log(level, AuthEvent, "{Action} by {Actor} on {Target}: {Outcome}", action, actor, target, outcome);
    }

    /// <summary>One request to a sign-in endpoint, or a refused request to the MCP listener. Pass the path only, never the query string.</summary>
    public static void LogAuthRequest(this ILogger logger, string listener, string method, string path, int status, long elapsedMs)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Log(
            status >= 500 ? LogLevel.Error : status >= 400 ? LogLevel.Warning : LogLevel.Information,
            AuthRequestEvent,
            "{Listener} {Method} {Path} -> {Status} in {ElapsedMs} ms",
            listener,
            method,
            path,
            status,
            elapsedMs);
    }

    /// <summary>A step in the life of a command run: waiting, approved, started, a first output line, finished.</summary>
    public static void LogCommand(this ILogger logger, LogLevel level, long runId, string stage, string workspace, string detail)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Log(level, CommandEvent, "#{RunId} {Stage}  {Workspace}  {Detail}", runId, stage, workspace, detail);
    }
}
