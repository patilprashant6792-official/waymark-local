using Waymark.Core.Memory;
using Waymark.Modules.Code.Memory.Semantic;

namespace Waymark.Tests.Memory;

public sealed class EmbeddingVectorsTests
{
    [Fact]
    public void AVectorSurvivesTheBlob()
    {
        float[] vector = [0.5f, -1.25f, 3f, float.Epsilon];

        Assert.Equal(vector, EmbeddingVectors.FromBlob(EmbeddingVectors.ToBlob(vector)));
    }

    [Fact]
    public void NormalizeMakesLengthOne()
    {
        float[] vector = [3f, 4f];

        Assert.True(EmbeddingVectors.Normalize(vector));
        Assert.Equal(1.0, EmbeddingVectors.Dot(vector, vector), 5);
        Assert.Equal(0.6, vector[0], 5);
    }

    [Fact]
    public void AZeroOrNonFiniteVectorIsRefused()
    {
        Assert.False(EmbeddingVectors.Normalize([0f, 0f]));
        Assert.False(EmbeddingVectors.Normalize([float.NaN, 1f]));
    }

    [Fact]
    public void TheDotProductMatchesALongVector()
    {
        var a = Enumerable.Range(1, 37).Select(i => (float)i).ToArray();
        var b = Enumerable.Repeat(1f, 37).ToArray();

        Assert.Equal(703.0, EmbeddingVectors.Dot(a, b), 3);
        Assert.Equal(0.0, EmbeddingVectors.Dot(a, [1f]));
    }

    [Fact]
    public void RankingIsByScoreThenStable()
    {
        var ranked = MemoryVectorCache.Rank(
            [new VectorHit(EmbeddingItemKind.Note, 2, 0.5), new VectorHit(EmbeddingItemKind.Flow, 9, 0.9), new VectorHit(EmbeddingItemKind.Note, 1, 0.5)],
            2);

        Assert.Equal([9L, 1L], ranked.Select(hit => hit.ItemId));
    }
}
