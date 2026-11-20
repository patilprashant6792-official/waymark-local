using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Waymark.Core.Memory;
using Waymark.Core.Security;

namespace Waymark.Host.Memory.Providers;

/// <summary>
/// What every embeddings API client shares: the named HttpClient (15 s for documents, 3 s for a query), the mapping of HTTP failures to
/// <see cref="EmbeddingFailure"/>, Retry-After, and the checks on the answer (one vector per text, the asked dimension, finite values), then L2 normalisation.
/// Request bodies, headers and keys are never logged or put into an exception message.
/// </summary>
public abstract class HttpEmbeddingProvider(IHttpClientFactory http, ILogger logger) : IEmbeddingProvider
{
    public const string DocumentClient = "Waymark.Memory.Document";
    public const string QueryClient = "Waymark.Memory.Query";

    /// <summary>The longest text sent, in characters; longer ones are cut (notes are at most 1,200, a flow's text a few thousand).</summary>
    public const int MaxTextChars = 16_000;

    private const int MaxErrorChars = 160;

    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public abstract string DefaultModel { get; }

    public abstract int MaxBatchTexts { get; }

    public async Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, int dimension, string model, string apiKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0 || texts.Count > MaxBatchTexts)
        {
            throw new ArgumentException($"{DisplayName} takes 1 to {MaxBatchTexts} texts per request.", nameof(texts));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new EmbeddingException(EmbeddingFailure.Unauthorized, $"No {DisplayName} key is saved.");
        }

        var cut = texts.Select(text => text.Length <= MaxTextChars ? text : text[..MaxTextChars]).ToList();
        using var request = BuildRequest(cut, purpose, dimension, model, apiKey);
        var client = http.CreateClient(purpose == EmbeddingPurpose.Query ? QueryClient : DocumentClient);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new EmbeddingException(EmbeddingFailure.Transient, $"{DisplayName} did not answer in time.", null, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new EmbeddingException(EmbeddingFailure.Transient, $"{DisplayName} could not be reached ({ex.HttpRequestError}).", null, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await FailureAsync(response, ct).ConfigureAwait(false);
            }

            JsonDocument document;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new EmbeddingException(EmbeddingFailure.Transient, $"{DisplayName} sent an answer that is not JSON.", null, ex);
            }

            using (document)
            {
                IReadOnlyList<float[]> vectors;
                try
                {
                    vectors = ParseVectors(document.RootElement, cut.Count);
                }
                catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException or IndexOutOfRangeException)
                {
                    throw new EmbeddingException(EmbeddingFailure.Transient, $"{DisplayName} sent an answer of an unexpected shape.", null, ex);
                }

                return Check(vectors, cut.Count, dimension);
            }
        }
    }

    /// <summary>The request for these texts; the key goes into a header, never the URL's query or the log.</summary>
    protected abstract HttpRequestMessage BuildRequest(IReadOnlyList<string> texts, EmbeddingPurpose purpose, int dimension, string model, string apiKey);

    /// <summary>One vector per input, in input order.</summary>
    protected abstract IReadOnlyList<float[]> ParseVectors(JsonElement root, int count);

    protected static HttpRequestMessage JsonPost(string url, JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    protected static JsonArray Strings(IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var array = new JsonArray();
        foreach (var text in texts)
        {
            array.Add((JsonNode?)JsonValue.Create(text));
        }

        return array;
    }

    protected static float[] Floats(JsonElement array)
    {
        var vector = new float[array.GetArrayLength()];
        var i = 0;
        foreach (var value in array.EnumerateArray())
        {
            vector[i++] = value.GetSingle();
        }

        return vector;
    }

    /// <summary>OpenAI-style answers: data[] with an index and an embedding each, put back in input order.</summary>
    protected static IReadOnlyList<float[]> ParseIndexedData(JsonElement root, int count)
    {
        var vectors = new float[count][];
        foreach (var item in root.GetProperty("data").EnumerateArray())
        {
            var index = item.TryGetProperty("index", out var at) ? at.GetInt32() : -1;
            if (index < 0 || index >= count)
            {
                throw new InvalidOperationException("An embedding has no valid index.");
            }

            vectors[index] = Floats(item.GetProperty("embedding"));
        }

        return vectors.Any(vector => vector is null) ? throw new InvalidOperationException("An embedding is missing.") : vectors;
    }

    private EmbeddingBatch Check(IReadOnlyList<float[]> vectors, int count, int dimension)
    {
        if (vectors.Count != count)
        {
            throw new EmbeddingException(EmbeddingFailure.Transient, $"{DisplayName} returned {vectors.Count} embeddings for {count} texts.");
        }

        foreach (var vector in vectors)
        {
            if (vector.Length != dimension)
            {
                throw new EmbeddingException(
                    EmbeddingFailure.BadRequest,
                    $"{DisplayName} returned vectors of {vector.Length} numbers, not the {dimension} asked for. Choose a dimension this model offers.");
            }

            if (!EmbeddingVectors.Normalize(vector))
            {
                throw new EmbeddingException(EmbeddingFailure.BadRequest, $"{DisplayName} returned an empty or invalid vector.");
            }
        }

        return new EmbeddingBatch(vectors, dimension);
    }

    private async Task<EmbeddingException> FailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        logger.LogWarning("The {Provider} embeddings request failed with status {Status}.", DisplayName, status);
        var detail = await ErrorDetailAsync(response, ct).ConfigureAwait(false);
        var text = string.Create(CultureInfo.InvariantCulture, $"{DisplayName} answered {status} {response.ReasonPhrase}{(detail is null ? string.Empty : ": " + detail)}");
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new EmbeddingException(EmbeddingFailure.Unauthorized, text + ". Check the key on the Setup page."),
            HttpStatusCode.TooManyRequests => new EmbeddingException(EmbeddingFailure.RateLimited, text, RetryAfter(response)),
            HttpStatusCode.RequestTimeout => new EmbeddingException(EmbeddingFailure.Transient, text),
            _ when status >= 500 => new EmbeddingException(EmbeddingFailure.Transient, text, RetryAfter(response)),
            _ => new EmbeddingException(EmbeddingFailure.BadRequest, text),
        };
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }

    // The provider's own error message, short and checked for secrets, because 'model not found' is what the owner needs to see. Never the request.
    private static async Task<string?> ErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            string? message = null;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            {
                message = error.ValueKind == JsonValueKind.String ? error.GetString()
                    : error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var inner) && inner.ValueKind == JsonValueKind.String ? inner.GetString()
                    : null;
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
            {
                message = detail.GetString();
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            var flat = SensitiveDataScanner.Redact(message.Replace('\n', ' ').Replace('\r', ' ').Trim()).Text;
            return flat.Length <= MaxErrorChars ? flat : flat[..MaxErrorChars] + "…";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or HttpRequestException)
        {
            return null;
        }
    }
}
