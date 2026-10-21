using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Waymark.Core.LiveFeed;

namespace Waymark.Logging;

/// <summary>
/// A standard logging provider that keeps very little on purpose: the entries made with <see cref="ActivityLogging"/> (tool calls, file
/// changes, sign-in events, commands) and any error from anywhere. Everything else is ignored. What is kept keeps its structured
/// fields, is scrubbed of credentials and cut to a short length, and goes to the in-memory hub. It writes nowhere else.
/// </summary>
[ProviderAlias("Live")]
public sealed class LiveLogProvider(LiveLogHub hub, LiveLogOptions options, TimeProvider time) : ILoggerProvider, ISupportExternalScope
{
    [ThreadStatic]
    private static bool _capturing;

    private IExternalScopeProvider? _scopes;

    public ILogger CreateLogger(string categoryName) => new LiveLogger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
        // Nothing to release: the hub is owned by the container.
    }

    private static string Cut(string value, int max) =>
        value.Length <= max ? value : string.Create(CultureInfo.InvariantCulture, $"{value.AsSpan(0, max)}... (+{value.Length - max} chars)");

    private void Capture<TState>(string category, LogLevel level, LogKind kind, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? template = null;
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var pair in pairs)
            {
                if (pair.Key == "{OriginalFormat}")
                {
                    template = pair.Value as string;
                    continue;
                }

                AddField(fields, pair.Key, pair.Value);
            }
        }

        string message;
        try
        {
            message = formatter(state, exception);
        }
        catch (FormatException)
        {
            message = template ?? string.Empty;
        }

        if (message.Length == 0 && exception is not null)
        {
            message = exception.Message;
        }

        var entry = new LogEntry(
            0,
            time.GetUtcNow(),
            level,
            kind,
            category,
            Cut(LogScrubber.Scrub(message.TrimEnd()), options.MaxMessageLength),
            template,
            fields,
            exception is null ? null : Cut(LogScrubber.Scrub(exception.ToString()), options.MaxExceptionLength),
            FindCorrelation());
        hub.Append(entry);
    }

    private string? FindCorrelation()
    {
        var holder = new CorrelationHolder();
        _scopes?.ForEachScope(
            static (scope, found) =>
            {
                if (found.Value is not null || scope is not IEnumerable<KeyValuePair<string, object?>> scopePairs)
                {
                    return;
                }

                foreach (var pair in scopePairs)
                {
                    if (pair.Key is "CorrelationId" or "TraceId" or "RequestId" && pair.Value is { } value)
                    {
                        found.Value = value.ToString();
                        return;
                    }
                }
            },
            holder);

        return holder.Value ?? Activity.Current?.TraceId.ToString();
    }

    private void AddField(Dictionary<string, string> fields, string key, object? value)
    {
        if (fields.Count >= options.MaxFields || string.IsNullOrEmpty(key))
        {
            return;
        }

        var name = key.Length <= 64 ? key : key[..64];
        if (LogScrubber.IsSensitiveKey(name))
        {
            fields[name] = LogScrubber.Hidden;
            return;
        }

        string text;
        try
        {
            text = value switch
            {
                null => "null",
                string s => s,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty
            };
        }
        catch (Exception)
        {
            text = "(could not be shown)";
        }

        fields[name] = Cut(LogScrubber.Scrub(text), options.MaxValueLength);
    }

    private sealed class CorrelationHolder
    {
        public string? Value { get; set; }
    }

    private sealed class LiveLogger(string category, LiveLogProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner._scopes?.Push(state);

        // Debug and trace are never kept, so callers can skip building them.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (_capturing || !IsEnabled(logLevel))
            {
                return;
            }

            // The allow-list: one of the four streams, or an error from anywhere.
            var kind = LogEvents.KindOf(eventId);
            if (kind == LogKind.Other && logLevel < LogLevel.Error)
            {
                return;
            }

            _capturing = true;
            try
            {
                owner.Capture(category, logLevel, kind, state, exception, formatter);
            }
            catch (Exception)
            {
                // Showing a log entry must never break the code that logged it.
            }
            finally
            {
                _capturing = false;
            }
        }
    }
}
