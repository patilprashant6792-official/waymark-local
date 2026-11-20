using System.Text.Json;
using System.Text.Json.Nodes;
using Waymark.Core.Memory;

namespace Waymark.Host.Memory.Providers;

/// <summary>
/// Google Gemini: POST https://generativelanguage.googleapis.com/v1beta/models/{model}:batchEmbedContents with the key in the x-goog-api-key header.
/// Each request has the model ('models/{model}'), content.parts[].text and an embedContentConfig with outputDimensionality; gemini-embedding-001 also
/// takes taskType RETRIEVAL_DOCUMENT or RETRIEVAL_QUERY there, while gemini-embedding-2 refuses taskType and takes the task as a prefix of the text.
/// The top-level taskType, title and outputDimensionality fields are deprecated in favour of embedContentConfig. Answer: embeddings[].values in request order.
/// Vectors of a reduced dimension are not normalised by gemini-embedding-001; the base class normalises every vector.
/// </summary>
public sealed class GeminiEmbeddingProvider(IHttpClientFactory http, ILogger<GeminiEmbeddingProvider> logger) : HttpEmbeddingProvider(http, logger)
{
    public const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";

    public override string Id => "gemini";

    public override string DisplayName => "Google Gemini";

    /// <summary>The text embedding model with task types; gemini-embedding-2 (multimodal) works too when chosen.</summary>
    public override string DefaultModel => "gemini-embedding-001";

    public override int MaxBatchTexts => 100;

    /// <summary>Models that take the task in the text (the gemini-embedding-2 family) rather than as taskType.</summary>
    internal static bool UsesPromptTasks(string model) => model.StartsWith("gemini-embedding-2", StringComparison.OrdinalIgnoreCase);

    /// <summary>The text as sent: unchanged for task-type models; with the retrieval prefix for prompt-task models.</summary>
    internal static string Prepare(string text, EmbeddingPurpose purpose, string model) =>
        !UsesPromptTasks(model) ? text
        : purpose == EmbeddingPurpose.Query ? "task: search result | query: " + text
        : "title: none | text: " + text;

    protected override HttpRequestMessage BuildRequest(IReadOnlyList<string> texts, EmbeddingPurpose purpose, int dimension, string model, string apiKey)
    {
        var name = "models/" + model;
        var requests = new JsonArray();
        foreach (var text in texts)
        {
            var config = new JsonObject { ["outputDimensionality"] = dimension };
            if (!UsesPromptTasks(model))
            {
                config["taskType"] = purpose == EmbeddingPurpose.Query ? "RETRIEVAL_QUERY" : "RETRIEVAL_DOCUMENT";
            }

            var item = new JsonObject
            {
                ["model"] = name,
                ["content"] = new JsonObject
                {
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = Prepare(text, purpose, model) }),
                },
                ["embedContentConfig"] = config,
            };

            requests.Add(item);
        }

        var request = JsonPost(BaseUrl + Uri.EscapeDataString(model) + ":batchEmbedContents", new JsonObject { ["requests"] = requests });
        request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        return request;
    }

    protected override IReadOnlyList<float[]> ParseVectors(JsonElement root, int count) =>
        [.. root.GetProperty("embeddings").EnumerateArray().Select(item => Floats(item.GetProperty("values")))];
}
