using System.Net;
using Waymark.Core.Memory;

namespace Waymark.Tests.Memory;

/// <summary>A search that finds nothing, for services that only use it for hints.</summary>
internal sealed class EmptyMemorySearch : IMemorySearch
{
    public Task<MemorySearchResult> SearchAsync(long workspaceId, string query, int take, CancellationToken ct) =>
        Task.FromResult(new MemorySearchResult([], false, null));
}

/// <summary>Search-by-meaning settings in memory. Off unless a test turns it on.</summary>
internal sealed class FakeMemorySettings : IMemorySettings
{
    public bool Enabled { get; set; }

    public string Provider { get; set; } = "fake";

    public string Model { get; set; } = "model-a";

    public int Dimension { get; set; } = 64;

    public string? Key { get; set; } = "test-key";

    public HashSet<long> Excluded { get; } = [];

    public Task<MemorySettings> GetAsync(CancellationToken ct) => Task.FromResult(new MemorySettings(Enabled, Provider, Model, Dimension, Key is not null));

    public Task<string?> GetKeyAsync(string provider, CancellationToken ct) => Task.FromResult(Key);

    public Task<MemoryKeyInfo> GetKeyInfoAsync(string provider, CancellationToken ct) => Task.FromResult(new MemoryKeyInfo(provider, Key is not null, null, false));

    public Task<bool> IsExcludedAsync(long workspaceId, CancellationToken ct) => Task.FromResult(Excluded.Contains(workspaceId));

    public Task SetEnabledAsync(bool enabled, string changedBy, CancellationToken ct)
    {
        Enabled = enabled;
        return Task.CompletedTask;
    }

    public Task SetModelAsync(string provider, string model, int dimension, string changedBy, CancellationToken ct)
    {
        (Provider, Model, Dimension) = (provider, model, dimension);
        return Task.CompletedTask;
    }

    public Task SaveKeyAsync(string provider, string key, string changedBy, CancellationToken ct)
    {
        Key = key;
        return Task.CompletedTask;
    }

    public Task ClearKeyAsync(string provider, string changedBy, CancellationToken ct)
    {
        Key = null;
        return Task.CompletedTask;
    }

    public Task SetExcludedAsync(long workspaceId, bool excluded, string changedBy, CancellationToken ct)
    {
        if (excluded)
        {
            Excluded.Add(workspaceId);
        }
        else
        {
            Excluded.Remove(workspaceId);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// An embedding provider that never leaves the process: a text that contains a word of <see cref="Meanings"/> gets that word's vector, any other text a fixed one.
/// It counts calls and texts, and can be told to fail.
/// </summary>
internal sealed class FakeEmbeddingProvider : IEmbeddingProvider, IEmbeddingProviders
{
    public Dictionary<string, float[]> Meanings { get; } = new(StringComparer.OrdinalIgnoreCase);

    public EmbeddingException? FailWith { get; set; }

    public int Calls { get; private set; }

    public List<string> Texts { get; } = [];

    public string Id => "fake";

    public string DisplayName => "Fake";

    public string DefaultModel => "model-a";

    public int MaxBatchTexts => 10;

    public IReadOnlyList<IEmbeddingProvider> All => [this];

    public IEmbeddingProvider? Find(string? id) => string.Equals(id, Id, StringComparison.OrdinalIgnoreCase) ? this : null;

    public Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, int dimension, string model, string apiKey, CancellationToken ct)
    {
        Calls++;
        Texts.AddRange(texts);
        if (FailWith is not null)
        {
            throw FailWith;
        }

        var vectors = texts.Select(text => VectorFor(text, dimension)).ToList();
        return Task.FromResult(new EmbeddingBatch(vectors, dimension));
    }

    private float[] VectorFor(string text, int dimension)
    {
        foreach (var (word, meaning) in Meanings)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                var copy = meaning.ToArray();
                EmbeddingVectors.Normalize(copy);
                return copy;
            }
        }

        var fallback = new float[dimension];
        fallback[^1] = 1f;
        return fallback;
    }
}

/// <summary>Answers every request with one canned response and keeps the requests it saw.</summary>
internal sealed class FakeHttpHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public string? RetryAfterSeconds { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, content));
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        if (RetryAfterSeconds is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", RetryAfterSeconds);
        }

        return response;
    }
}

/// <summary>Hands out clients over one handler.</summary>
internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
