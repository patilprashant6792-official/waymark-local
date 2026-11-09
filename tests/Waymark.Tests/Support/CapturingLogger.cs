using Microsoft.Extensions.Logging;

namespace Waymark.Tests.Support;

internal sealed record CapturedLog(LogLevel Level, string Message, Exception? Exception);

/// <summary>Keeps every log call so a test can assert that a failure was reported and not swallowed.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<CapturedLog> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add(new CapturedLog(logLevel, formatter(state, exception), exception));
        }
    }
}
