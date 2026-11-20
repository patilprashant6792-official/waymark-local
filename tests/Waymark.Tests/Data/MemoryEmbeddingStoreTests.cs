using Waymark.Core.Memory;
using Waymark.Data.Stores;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class MemoryEmbeddingStoreTests
{
    private const int Size = 64;
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task VectorsAreStoredReplacedAndLoadedPerModel()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, ws) = await SetUpAsync(db);

        await store.UpsertAsync([Row(ws, 1, "m@64", "h1", 0), Row(ws, 2, "m@64", "h2", 1), Row(ws, 1, "other@64", "h1", 2)], Now, _ct);
        await store.UpsertAsync([Row(ws, 1, "m@64", "h1b", 3)], Now, _ct);

        var loaded = await store.LoadAsync(ws, "m@64", _ct);
        Assert.Equal(2, loaded.Count);
        var first = loaded.Single(row => row.ItemId == 1);
        Assert.Equal("h1b", first.TextHash);
        Assert.Equal(Hot(3), first.Vector);
        Assert.Equal(2, await store.CountAsync("m@64", _ct));
    }

    [Fact]
    public async Task OtherModelsAndMissingItemsAreDeleted()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, ws) = await SetUpAsync(db);
        await store.UpsertAsync([Row(ws, 1, "m@64", "h", 0), Row(ws, 2, "m@64", "h", 0), Row(ws, 1, "old@64", "h", 0)], Now, _ct);

        Assert.Equal(1, await store.DeleteOtherModelsAsync(ws, "m@64", _ct));
        Assert.Equal(1, await store.DeleteMissingAsync(ws, "m@64", [(EmbeddingItemKind.Note, 1L)], _ct));
        Assert.Equal([1L], (await store.ListStampsAsync(ws, "m@64", _ct)).Select(stamp => stamp.ItemId));
    }

    [Fact]
    public async Task DeletingAWorkspaceOrAFlowRemovesItsVectors()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, ws) = await SetUpAsync(db);
        var maps = new FeatureMapStore(db.Connections, db.Writes);
        var map = await maps.CreateAsync(ws, "f", "s", string.Empty, [new FeatureMapStep("a.cs", 0, "call", string.Empty)], Now, _ct);
        await store.UpsertAsync([Row(ws, 5, "m@64", "h", 0), new StoredEmbedding(ws, EmbeddingItemKind.Flow, map.Id, "m@64", Size, "h", Hot(1))], Now, _ct);

        await maps.DeleteAsync(ws, "f", _ct);
        Assert.Equal(1, await store.CountAsync("m@64", _ct));

        await new WorkspaceStore(db.Connections, db.Writes).RemoveAsync(ws, _ct);
        Assert.Equal(0, await store.CountAsync("m@64", _ct));
    }

    internal static float[] Hot(int index)
    {
        var vector = new float[Size];
        vector[index] = 1f;
        return vector;
    }

    private static StoredEmbedding Row(long ws, long id, string model, string hash, int hot) =>
        new(ws, EmbeddingItemKind.Note, id, model, Size, hash, Hot(hot));

    private static async Task<(MemoryEmbeddingStore Store, long WorkspaceId)> SetUpAsync(TestDatabase db)
    {
        var workspace = await new WorkspaceStore(db.Connections, db.Writes).AddAsync("w", "C:/w", Now, TestContext.Current.CancellationToken);
        return (new MemoryEmbeddingStore(db.Connections, db.Writes), workspace!.Id);
    }
}
