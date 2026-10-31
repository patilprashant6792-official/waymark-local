using Waymark.Core.LiveFeed;
using Waymark.Logging;

namespace Waymark.Host.Ui;

/// <summary>The words and style classes that describe a live log line. The Live page and the Home live card share them so a line looks the same in both.</summary>
public static class LiveLineFormat
{
    public static string KindWord(LogKind kind) => kind switch
    {
        LogKind.Tool => "tool",
        LogKind.Watcher => "watch",
        LogKind.Auth => "auth",
        LogKind.Command => "cmd",
        _ => "app"
    };

    public static string LevelClass(LogLevel level) => level switch
    {
        >= LogLevel.Error => "err",
        LogLevel.Warning => "warn",
        _ => string.Empty
    };

    // Colour alone is not enough: warnings and errors also carry a word.
    public static string LevelWord(LogLevel level) => level switch
    {
        >= LogLevel.Error => "ERR",
        LogLevel.Warning => "WARN",
        _ => string.Empty
    };

    /// <summary>The audit reference of the tool call a live line shows, so the line can link to the call's details; null for any other line, or when the audit row could not be written.</summary>
    public static long? CallIdOf(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Kind == LogKind.Tool
            && entry.Fields.TryGetValue(ActivityLogging.CallIdField, out var text)
            && long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
                ? id
                : null;
    }
}
