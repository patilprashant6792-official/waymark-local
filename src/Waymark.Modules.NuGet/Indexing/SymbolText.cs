using Waymark.Core.Text;

namespace Waymark.Modules.NuGet.Indexing;

/// <summary>Identifier tokenisation shared by the indexer and the query path; they must agree exactly. The rules live in <see cref="IdentifierWords"/>.</summary>
internal static class SymbolText
{
    public static IEnumerable<string> Tokenize(string? value) => IdentifierWords.Tokenize(value);
}
