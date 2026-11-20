using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Waymark.Core.Memory;

namespace Waymark.Host.Memory.Providers;

/// <summary>
/// Voyage AI: POST /v1/embeddings with a Bearer key; input (up to 1,000 texts), model, input_type document or query, output_dimension (256, 512, 1024 or 2048 on the voyage-4
/// and voyage-3.5 families), truncation on. A key from the Voyage dashboard starts 'pa-' and goes to api.voyageai.com; a MongoDB Atlas model key starts 'al-' and goes to ai.mongodb.com.
/// Answer: data[] with index and embedding.
/// </summary>
public sealed class VoyageEmbeddingProvider(IHttpClientFactory http, ILogger<VoyageEmbeddingProvider> logger) : HttpEmbeddingProvider(http, logger)
{
    public const string VoyageHost = "https://api.voyageai.com";
    public const string AtlasHost = "https://ai.mongodb.com";

    public override string Id => "voyage";

    public override string DisplayName => "Voyage AI";

    public override string DefaultModel => "voyage-4";

    public override int MaxBatchTexts => 128;

    protected override HttpRequestMessage BuildRequest(IReadOnlyList<string> texts, EmbeddingPurpose purpose, int dimension, string model, string apiKey)
    {
        var host = apiKey.StartsWith("al-", StringComparison.Ordinal) ? AtlasHost : VoyageHost;
        var body = new JsonObject
        {
            ["input"] = Strings(texts),
            ["model"] = model,
            ["input_type"] = purpose == EmbeddingPurpose.Query ? "query" : "document",
            ["output_dimension"] = dimension,
            ["truncation"] = true,
        };
        var request = JsonPost(host + "/v1/embeddings", body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    protected override IReadOnlyList<float[]> ParseVectors(JsonElement root, int count) => ParseIndexedData(root, count);
}
