using Microsoft.Extensions.Logging;

namespace Waymark.Core.LiveFeed;

/// <summary>Event-name prefixes that give an entry its kind. The Live page keeps an entry only when it has one of these, or when it is an error.</summary>
public static class LogEvents
{
    public const string ToolPrefix = "tool.";
    public const string WatcherPrefix = "watch.";
    public const string AuthPrefix = "auth.";
    public const string CommandPrefix = "cmd.";

    public static LogKind KindOf(EventId eventId)
    {
        var name = eventId.Name;
        if (name is null)
        {
            return LogKind.Other;
        }

        if (name.StartsWith(ToolPrefix, StringComparison.Ordinal))
        {
            return LogKind.Tool;
        }

        if (name.StartsWith(WatcherPrefix, StringComparison.Ordinal))
        {
            return LogKind.Watcher;
        }

        if (name.StartsWith(AuthPrefix, StringComparison.Ordinal))
        {
            return LogKind.Auth;
        }

        return name.StartsWith(CommandPrefix, StringComparison.Ordinal) ? LogKind.Command : LogKind.Other;
    }
}
