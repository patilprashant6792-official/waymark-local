using Waymark.Core.Memory;

namespace Waymark.Host.Memory;

/// <summary>The registered embedding providers, by id, in the order the Setup page lists them.</summary>
public sealed class EmbeddingProviders : IEmbeddingProviders
{
    public EmbeddingProviders(IEnumerable<IEmbeddingProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        All = [.. providers];
        if (All.Count == 0)
        {
            throw new InvalidOperationException("No embedding provider is registered.");
        }
    }

    public IReadOnlyList<IEmbeddingProvider> All { get; }

    public IEmbeddingProvider? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(provider => string.Equals(provider.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
}
