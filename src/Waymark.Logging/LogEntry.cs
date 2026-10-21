using Microsoft.Extensions.Logging;
using Waymark.Core.LiveFeed;

namespace Waymark.Logging;

/// <summary>One structured log event as held in memory for the Live page. Nothing here is ever written to disk.</summary>
public sealed record LogEntry(
    long Id,
    DateTimeOffset TimestampUtc,
    LogLevel Level,
    LogKind Kind,
    string Category,
    string Message,
    string? Template,
    IReadOnlyDictionary<string, string> Fields,
    string? Exception,
    string? CorrelationId);
