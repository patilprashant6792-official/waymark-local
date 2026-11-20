namespace Waymark.Core.Memory;

public enum EmbeddingItemKind
{
    Note,
    Flow,
}

/// <summary>One stored vector: what it is for, which model made it, and the hash of the text it was made from.</summary>
public sealed record StoredEmbedding(
    long WorkspaceId,
    EmbeddingItemKind Kind,
    long ItemId,
    string ModelId,
    int Dimension,
    string TextHash,
    float[] Vector);

/// <summary>What the index knows about an item without loading its vector: enough to tell whether it is pending.</summary>
public sealed record EmbeddingStamp(EmbeddingItemKind Kind, long ItemId, string TextHash);

/// <summary>
/// The vectors of notes and flows (table memory_embeddings). One row per item and model id; vectors are float32 little-endian and L2-normalised.
/// There is no queue: an item is pending when its current text hash has no row for the active model.
/// </summary>
public interface IMemoryEmbeddingStore
{
    /// <summary>Adds or replaces the rows, in one transaction.</summary>
    Task UpsertAsync(IReadOnlyList<StoredEmbedding> rows, DateTimeOffset now, CancellationToken ct);

    Task DeleteItemAsync(EmbeddingItemKind kind, long itemId, CancellationToken ct);

    /// <summary>Every row of the workspace, any model. The number removed.</summary>
    Task<int> DeleteWorkspaceAsync(long workspaceId, CancellationToken ct);

    /// <summary>Every row, any workspace and model (the owner's Delete stored vectors). The number removed.</summary>
    Task<int> DeleteAllAsync(CancellationToken ct);

    /// <summary>The rows of models other than <paramref name="keepModelId"/>, in this workspace. The number removed.</summary>
    Task<int> DeleteOtherModelsAsync(long workspaceId, string keepModelId, CancellationToken ct);

    /// <summary>The rows of items no longer in <paramref name="keep"/> (deleted flows, retired or revised notes) for this workspace and model. The number removed.</summary>
    Task<int> DeleteMissingAsync(long workspaceId, string modelId, IReadOnlyCollection<(EmbeddingItemKind Kind, long ItemId)> keep, CancellationToken ct);

    Task<IReadOnlyList<EmbeddingStamp>> ListStampsAsync(long workspaceId, string modelId, CancellationToken ct);

    Task<IReadOnlyList<StoredEmbedding>> LoadAsync(long workspaceId, string modelId, CancellationToken ct);

    /// <summary>How many rows exist for this model, all workspaces.</summary>
    Task<int> CountAsync(string modelId, CancellationToken ct);
}

/// <summary>Float vectors as stored: float32 little-endian, and the normalisation every stored vector has.</summary>
public static class EmbeddingVectors
{
    public static byte[] ToBlob(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    /// <exception cref="ArgumentException">The length is not a whole number of floats.</exception>
    public static float[] FromBlob(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length % sizeof(float) != 0)
        {
            throw new ArgumentException("A vector blob holds whole float32 values.", nameof(blob));
        }

        var vector = new float[blob.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(i * sizeof(float)));
        }

        return vector;
    }

    /// <summary>The vector scaled to length 1, in place. A zero vector is left as it is and reported.</summary>
    /// <returns>False for a zero or non-finite vector, which must not be stored.</returns>
    public static bool Normalize(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        double sum = 0;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value))
            {
                return false;
            }

            sum += (double)value * value;
        }

        if (sum <= 0)
        {
            return false;
        }

        var scale = (float)(1.0 / Math.Sqrt(sum));
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] *= scale;
        }

        return true;
    }

    /// <summary>The dot product, which is the cosine similarity of two normalised vectors. Vectors of different lengths are not comparable: 0.</summary>
    public static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            return 0;
        }

        var sum = 0f;
        var i = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated && a.Length >= System.Numerics.Vector<float>.Count)
        {
            var acc = System.Numerics.Vector<float>.Zero;
            var width = System.Numerics.Vector<float>.Count;
            for (; i <= a.Length - width; i += width)
            {
                acc += new System.Numerics.Vector<float>(a.Slice(i, width)) * new System.Numerics.Vector<float>(b.Slice(i, width));
            }

            sum = System.Numerics.Vector.Sum(acc);
        }

        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}
