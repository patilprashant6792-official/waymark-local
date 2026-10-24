namespace Waymark.Core.Code;

/// <summary>
/// Renames in C# source by syntax, not by meaning. <see cref="Namespaces"/> maps a dotted namespace to a new dotted namespace; a name inside it follows (Legacy.Billing.Models becomes App.Orders.Models).
/// <see cref="Identifiers"/> maps one identifier to another and renames every identifier token with that name (never text in strings or comments).
/// </summary>
public sealed record CSharpRewriteMap(IReadOnlyDictionary<string, string> Namespaces, IReadOnlyDictionary<string, string> Identifiers)
{
    public bool IsEmpty => Namespaces.Count == 0 && Identifiers.Count == 0;
}

/// <summary>The rewritten text, how many places were renamed, and how many syntax errors the input already had (a fragment of a class has some).</summary>
public sealed record CSharpRewriteResult(string Text, int NamespaceHits, int IdentifierHits, int SyntaxErrors);

public interface ICSharpRewriter
{
    /// <exception cref="ArgumentException">A name in the map is not a valid C# name, or is a keyword.</exception>
    CSharpRewriteResult Rewrite(string text, CSharpRewriteMap map);
}
