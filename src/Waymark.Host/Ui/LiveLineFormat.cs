using Waymark.Core.LiveFeed;

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
}
