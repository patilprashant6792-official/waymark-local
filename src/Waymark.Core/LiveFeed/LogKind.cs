namespace Waymark.Core.LiveFeed;

/// <summary>Which stream of the Live page an entry belongs to. Other is anything logged without one of the helpers; only errors from it are kept.</summary>
public enum LogKind
{
    Other,
    Tool,
    Watcher,
    Auth,
    Command
}
