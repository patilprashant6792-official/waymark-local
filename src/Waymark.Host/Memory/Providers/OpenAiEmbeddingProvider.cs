using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Waymark.Core.Memory;

namespace Waymark.Host.Memory.Providers;

/// <summary>
/// OpenAI: POST https://api.openai.com/v1/embeddings with a Bearer key; input (up to 2,048 texts, 300,000 tokens per request), model, dimensions (text-embedding-3 models),
/// encoding_format float. There is no task type: documents and queries are embedded the same way. Answer: data[] with index and embedding.
/// </summary>
public sealed class OpenAiEmbeddingProvider(IHttpClientFactory http, ILogger<OpenAiEmbeddingProvider> logger) : HttpEmbeddingProvider(http, logger)
{
    public const string Endpoint = "https://api.openai.com/v1/embeddings";

    public override string Id => "openai";

    public override string DisplayName => "OpenAI";

    public override string DefaultModel => "text-embedding-3-large";

    public override int MaxBatchTexts => 256;

    protected override HttpRequestMessage BuildRequest(IReadOnlyList<string> texts, EmbeddingPurpose purpose, int dimension, string model, string apiKey)
    {
        var body = new JsonObject
        {
            ["input"] = Strings(texts),
            ["model"] = model,
            ["dimensions"] = dimension,
            ["encoding_format"] = "float",
        };
        var request = JsonPost(Endpoint, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    protected override IReadOnlyList<float[]> ParseVectors(JsonElement root, int count) => ParseIndexedData(root, count);
}
