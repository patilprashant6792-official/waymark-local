using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Waymark.Core.Memory;
using Waymark.Host.Memory.Providers;

namespace Waymark.Tests.Memory;

public sealed class EmbeddingProviderTests
{
    private const string Key = "pa-testkey-0000000000000000000000000000000000";
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static string IndexedAnswer(params float[][] vectors) =>
        JsonSerializer.Serialize(new { data = vectors.Select((vector, i) => new { index = i, embedding = vector }).Reverse().ToArray() });

    [Fact]
    public async Task VoyageSendsTheDocumentTypeTheDimensionAndABearerKey()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, IndexedAnswer([3f, 4f], [0f, 2f]));
        var provider = new VoyageEmbeddingProvider(new FakeHttpClientFactory(handler), NullLogger<VoyageEmbeddingProvider>.Instance);

        var batch = await provider.EmbedAsync(["one", "two"], EmbeddingPurpose.Document, 2, "voyage-4", Key, _ct);

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://api.voyageai.com/v1/embeddings", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("document", json.RootElement.GetProperty("input_type").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("output_dimension").GetInt32());
        Assert.Equal("voyage-4", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("input").GetArrayLength());
        Assert.Equal(0.6, batch.Vectors[0][0], 5);
        Assert.Equal(1.0, batch.Vectors[1][1], 5);
    }

    [Fact]
    public async Task AnAtlasKeyGoesToMongoDb()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, IndexedAnswer([1f, 0f]));
        var provider = new VoyageEmbeddingProvider(new FakeHttpClientFactory(handler), NullLogger<VoyageEmbeddingProvider>.Instance);

        await provider.EmbedAsync(["q"], EmbeddingPurpose.Query, 2, "voyage-4", "al-atlaskey", _ct);

        Assert.StartsWith(VoyageEmbeddingProvider.AtlasHost, handler.Requests[0].Request.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"input_type\":\"query\"", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiSendsDimensionsAndNoTaskType()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, IndexedAnswer([1f, 1f]));
        var provider = new OpenAiEmbeddingProvider(new FakeHttpClientFactory(handler), NullLogger<OpenAiEmbeddingProvider>.Instance);

        await provider.EmbedAsync(["q"], EmbeddingPurpose.Query, 2, "text-embedding-3-large", "sk-test", _ct);

        using var json = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal(2, json.RootElement.GetProperty("dimensions").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("input_type", out _));
        Assert.Equal(OpenAiEmbeddingProvider.Endpoint, handler.Requests[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task GeminiPutsTheKeyInAHeaderAndMapsThePurpose()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "{\"embeddings\":[{\"values\":[1,0]},{\"values\":[0,1]}]}");
        var provider = new GeminiEmbeddingProvider(new FakeHttpClientFactory(handler), NullLogger<GeminiEmbeddingProvider>.Instance);

        await provider.EmbedAsync(["a", "b"], EmbeddingPurpose.Query, 2, "gemini-embedding-001", "AIza-test", _ct);

        var (request, body) = handler.Requests[0];
        Assert.EndsWith("/models/gemini-embedding-001:batchEmbedContents", request.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.True(request.Headers.Contains("x-goog-api-key"));
        Assert.DoesNotContain("AIza-test", request.RequestUri.ToString(), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        var first = json.RootElement.GetProperty("requests")[0];
        Assert.Equal("models/gemini-embedding-001", first.GetProperty("model").GetString());
        var config = first.GetProperty("embedContentConfig");
        Assert.Equal("RETRIEVAL_QUERY", config.GetProperty("taskType").GetString());
        Assert.Equal(2, config.GetProperty("outputDimensionality").GetInt32());
        Assert.False(first.TryGetProperty("taskType", out _));
        Assert.False(first.TryGetProperty("outputDimensionality", out _));
    }

    [Fact]
    public async Task GeminiEmbedding2SendsNoTaskTypeAndPrefixesTheText()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "{\"embeddings\":[{\"values\":[1,0]}]}");
        var provider = new GeminiEmbeddingProvider(new FakeHttpClientFactory(handler), NullLogger<GeminiEmbeddingProvider>.Instance);

        await provider.EmbedAsync(["a"], EmbeddingPurpose.Document, 2, "gemini-embedding-2", "AIza-test", _ct);

        using var json = JsonDocument.Parse(handler.Requests[0].Body);
        var first = json.RootElement.GetProperty("requests")[0];
        var config = first.GetProperty("embedContentConfig");
        Assert.False(config.TryGetProperty("taskType", out _));
        Assert.Equal(2, config.GetProperty("outputDimensionality").GetInt32());
        Assert.Equal("title: none | text: a", first.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void GeminiEmbedding2TakesTheTaskInTheText()
    {
        Assert.StartsWith("task: search result | query: ", GeminiEmbeddingProvider.Prepare("x", EmbeddingPurpose.Query, "gemini-embedding-2"), StringComparison.Ordinal);
        Assert.Equal("x", GeminiEmbeddingProvider.Prepare("x", EmbeddingPurpose.Query, "gemini-embedding-001"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, EmbeddingFailure.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, EmbeddingFailure.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, EmbeddingFailure.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, EmbeddingFailure.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, EmbeddingFailure.Transient)]
    [InlineData(HttpStatusCode.BadRequest, EmbeddingFailure.BadRequest)]
    [InlineData(HttpStatusCode.NotFound, EmbeddingFailure.BadRequest)]
    public async Task FailuresAreMappedAndNeverCarryTheKey(HttpStatusCode status, EmbeddingFailure expected)
    {
        var handler = new FakeHttpHandler(status, "{\"error\":{\"message\":\"model not found\"}}") { RetryAfterSeconds = "7" };
        var provider = new OpenAiEmbeddingProvider(new FakeHttpClientFactory(handler), NullLogger<OpenAiEmbeddingProvider>.Instance);

        var ex = await Assert.ThrowsAsync<EmbeddingException>(() => provider.EmbedAsync(["q"], EmbeddingPurpose.Document, 2, "m", Key, _ct));

        Assert.Equal(expected, ex.Kind);
        Assert.DoesNotContain(Key, ex.ToString(), StringComparison.Ordinal);
        if (expected == EmbeddingFailure.RateLimited)
        {
            Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
        }
    }

    [Fact]
    public async Task AVectorOfTheWrongSizeIsRefused()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, IndexedAnswer([1f, 0f, 0f]));
        var provider = new OpenAiEmbeddingProvider(new FakeHttpClientFactory(handler), NullLogger<OpenAiEmbeddingProvider>.Instance);

        var ex = await Assert.ThrowsAsync<EmbeddingException>(() => provider.EmbedAsync(["q"], EmbeddingPurpose.Document, 2, "m", Key, _ct));

        Assert.Equal(EmbeddingFailure.BadRequest, ex.Kind);
    }

    [Fact]
    public async Task AnAnswerOfTheWrongShapeIsTransient()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "{\"nothing\":true}");
        var provider = new VoyageEmbeddingProvider(new FakeHttpClientFactory(handler), NullLogger<VoyageEmbeddingProvider>.Instance);

        var ex = await Assert.ThrowsAsync<EmbeddingException>(() => provider.EmbedAsync(["q"], EmbeddingPurpose.Document, 2, "m", Key, _ct));

        Assert.Equal(EmbeddingFailure.Transient, ex.Kind);
    }
}
