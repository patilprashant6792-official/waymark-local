using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Waymark.Core.LiveFeed;

namespace Waymark.Logging;

public static class LoggingBuilderExtensions
{
    /// <summary>
    /// Adds the live, memory-only log provider and the registry of what is happening now. The provider keeps only the four
    /// streams of <see cref="ActivityLogging"/> and errors; the Live page reads them. Nothing is stored anywhere else.
    /// </summary>
    public static ILoggingBuilder AddLiveLog(this ILoggingBuilder builder, Action<LiveLogOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new LiveLogOptions();
        configure?.Invoke(options);

        builder.Services.TryAddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<LiveLogHub>();
        builder.Services.TryAddSingleton<LiveActivity>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, LiveLogProvider>());
        return builder;
    }
}
