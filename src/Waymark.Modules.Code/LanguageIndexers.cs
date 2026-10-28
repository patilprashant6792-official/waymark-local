using System.Diagnostics.CodeAnalysis;
using Waymark.Core.Code;

namespace Waymark.Modules.Code;

/// <summary>Finds the indexer of a file by its extension. The one place that knows which languages the code index reads.</summary>
internal sealed class LanguageIndexers
{
    private readonly Dictionary<string, ILanguageIndexer> _byExtension = new(StringComparer.OrdinalIgnoreCase);

    public LanguageIndexers(IEnumerable<ILanguageIndexer> indexers)
    {
        ArgumentNullException.ThrowIfNull(indexers);
        foreach (var indexer in indexers)
        {
            foreach (var extension in indexer.Extensions)
            {
                if (!_byExtension.TryAdd(extension, indexer))
                {
                    throw new InvalidOperationException($"The extension '{extension}' is claimed by two language indexers.");
                }
            }
        }
    }

    /// <summary>The extensions that are indexed, each with its dot.</summary>
    public IReadOnlyCollection<string> Extensions => _byExtension.Keys;

    public bool TryGet(string path, [NotNullWhen(true)] out ILanguageIndexer? indexer)
    {
        indexer = null;
        var extension = Path.GetExtension(path);
        return extension.Length > 0 && _byExtension.TryGetValue(extension, out indexer);
    }

    public bool Handles(string path) => TryGet(path, out _);

    /// <exception cref="ArgumentException">The file is not one of the indexed languages.</exception>
    public ILanguageIndexer For(string path) =>
        TryGet(path, out var indexer)
            ? indexer
            : throw new ArgumentException($"'{path}' is not a source file the code tools can analyze ({Describe()}). Use code_read for other files.");

    /// <summary>The extensions as text for a message, for example <c>.cs, .py, .pyi</c>.</summary>
    public string Describe() => string.Join(", ", _byExtension.Keys.Order(StringComparer.OrdinalIgnoreCase));
}
