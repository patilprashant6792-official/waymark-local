namespace Waymark.Core.Memory;

/// <summary>What a text is embedded for. Providers that know task types embed documents and queries differently.</summary>
public enum EmbeddingPurpose
{
    Document,
    Query,
}

/// <summary>One vector per input text, in input order, each L2-normalised and of <paramref name="Dimension"/> floats.</summary>
public sealed record EmbeddingBatch(IReadOnlyList<float[]> Vectors, int Dimension);

public enum EmbeddingFailure
{
    /// <summary>The key was refused (401, 403). Retrying will not help until the key changes.</summary>
    Unauthorized,

    /// <summary>Too many requests (429). Retry after <see cref="EmbeddingException.RetryAfter"/> or a backoff.</summary>
    RateLimited,

    /// <summary>A timeout, a network failure or a server error (5xx). Retry with a backoff.</summary>
    Transient,

    /// <summary>The provider refused the request itself (400, 404, 422): a bad model name, a dimension it does not offer, an input it will not take.</summary>
    BadRequest,
}

/// <summary>An embedding request failed. The message names the provider and the status and never holds the key or the texts.</summary>
public sealed class EmbeddingException : Exception
{
    public EmbeddingException(EmbeddingFailure kind, string message, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        RetryAfter = retryAfter;
    }

    public EmbeddingFailure Kind { get; }

    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// One embeddings API (Voyage, OpenAI, Gemini). Stateless: the model, dimension and key come with every call, so switching the active provider needs no restart.
/// Implementations never log request bodies, headers or keys.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>'voyage', 'openai' or 'gemini': the value stored in the memory.provider setting.</summary>
    string Id { get; }

    /// <summary>The name shown on the Setup page.</summary>
    string DisplayName { get; }

    /// <summary>The model used when the owner has not chosen one.</summary>
    string DefaultModel { get; }

    /// <summary>The most texts one request may carry; callers split larger batches.</summary>
    int MaxBatchTexts { get; }

    /// <summary>Embeds the texts. Throws <see cref="EmbeddingException"/> for every failure of the provider, and <see cref="OperationCanceledException"/> when cancelled.</summary>
    Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, int dimension, string model, string apiKey, CancellationToken ct);
}

/// <summary>The embedding providers this install knows, by id.</summary>
public interface IEmbeddingProviders
{
    IReadOnlyList<IEmbeddingProvider> All { get; }

    /// <summary>The provider with this id (any letter case), or null.</summary>
    IEmbeddingProvider? Find(string? id);
}
