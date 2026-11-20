using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Waymark.Core.Memory;
using Waymark.Host.Memory.Providers;
using Waymark.Host.Notes;

namespace Waymark.Host.Memory;

public static class MemoryServiceCollectionExtensions
{
    private static readonly TimeSpan DocumentTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Registers Waymark Memory's host side: the embedding providers (Voyage, OpenAI, Gemini) and their HTTP clients, the encrypted keys and settings, the pinned-notes cache
    /// the workspace guide reads, and the admin services of the Notes and Setup pages. The index, sync and search live in the code module.
    /// </summary>
    public static IServiceCollection AddWaymarkMemory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpClient(HttpEmbeddingProvider.DocumentClient, client => Configure(client, DocumentTimeout));
        services.AddHttpClient(HttpEmbeddingProvider.QueryClient, client => Configure(client, QueryTimeout));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEmbeddingProvider, VoyageEmbeddingProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEmbeddingProvider, OpenAiEmbeddingProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEmbeddingProvider, GeminiEmbeddingProvider>());
        services.TryAddSingleton<IEmbeddingProviders, EmbeddingProviders>();
        services.TryAddSingleton<MemoryKeyStore>();
        services.TryAddSingleton<IMemorySettings, MemorySettingsService>();
        services.TryAddSingleton<IPinnedNotes, PinnedNotes>();
        services.TryAddSingleton<NoteAdminService>();
        services.TryAddSingleton<MemoryAdminService>();
        return services;
    }

    private static void Configure(HttpClient client, TimeSpan timeout)
    {
        client.Timeout = timeout;
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Waymark", "0.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}
