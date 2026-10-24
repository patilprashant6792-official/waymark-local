using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Waymark.Core.Code;

namespace Waymark.Modules.Code.Rewriting;

/// <summary>
/// Roslyn is used to find the names; the text is edited by position, so every space, comment and line ending outside the renamed names stays as it was.
/// Namespaces are renamed first, then identifiers on the result.
/// </summary>
internal sealed class CSharpRewriter : ICSharpRewriter
{
    private sealed record NamespaceRule(string[] Parts, string Replacement);

    private sealed record Edit(int Start, int End, string Text);

    public CSharpRewriteResult Rewrite(string text, CSharpRewriteMap map)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(map);
        var namespaces = BuildNamespaceRules(map.Namespaces);
        var identifiers = BuildIdentifierRules(map.Identifiers);

        var options = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse, SourceCodeKind.Regular);
        var current = text;
        var namespaceHits = 0;
        var syntaxErrors = 0;
        if (namespaces.Count > 0)
        {
            var tree = CSharpSyntaxTree.ParseText(current, options);
            syntaxErrors = CountErrors(tree);
            var edits = FindNamespaceEdits(tree, namespaces);
            namespaceHits = edits.Count;
            current = Apply(current, edits);
        }

        var identifierHits = 0;
        if (identifiers.Count > 0)
        {
            var tree = CSharpSyntaxTree.ParseText(current, options);
            syntaxErrors = Math.Max(syntaxErrors, CountErrors(tree));
            var edits = FindIdentifierEdits(tree, identifiers);
            identifierHits = edits.Count;
            current = Apply(current, edits);
        }

        return new CSharpRewriteResult(current, namespaceHits, identifierHits, syntaxErrors);
    }

    private static List<NamespaceRule> BuildNamespaceRules(IReadOnlyDictionary<string, string> namespaces)
    {
        var rules = new List<NamespaceRule>(namespaces.Count);
        foreach (var (from, to) in namespaces)
        {
            var parts = SplitName(from, "namespace");
            SplitName(to, "namespace");
            rules.Add(new NamespaceRule(parts, to.Trim()));
        }

        // The longest key wins, so Legacy.Billing.Models is not shadowed by Legacy.Billing.
        return [.. rules.OrderByDescending(r => r.Parts.Length)];
    }

    private static string[] SplitName(string name, string kind)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException($"An empty {kind} name cannot be renamed.");
        }

        var parts = name.Trim().Split('.');
        foreach (var part in parts)
        {
            CheckIdentifier(part, kind);
        }

        return parts;
    }

    private static Dictionary<string, string> BuildIdentifierRules(IReadOnlyDictionary<string, string> identifiers)
    {
        var rules = new Dictionary<string, string>(identifiers.Count, StringComparer.Ordinal);
        foreach (var (from, to) in identifiers)
        {
            CheckIdentifier(from.Trim(), "identifier");
            CheckIdentifier(to.Trim(), "identifier");
            rules[from.Trim()] = to.Trim();
        }

        return rules;
    }

    private static void CheckIdentifier(string name, string kind)
    {
        if (!SyntaxFacts.IsValidIdentifier(name) || SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None)
        {
            throw new ArgumentException($"'{name}' is not a valid {kind} name (a C# keyword or not an identifier).");
        }
    }

    private static int CountErrors(SyntaxTree tree) => tree.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);

    private static List<Edit> FindNamespaceEdits(SyntaxTree tree, List<NamespaceRule> rules)
    {
        var edits = new List<Edit>();
        foreach (var node in tree.GetRoot().DescendantNodes(descendIntoTrivia: true))
        {
            if (!IsChainTop(node))
            {
                continue;
            }

            var tokens = new List<SyntaxToken>();
            if (!TryFlatten(node, tokens))
            {
                continue;
            }

            var rule = rules.Find(r => Matches(r.Parts, tokens, node));
            if (rule is not null)
            {
                edits.Add(new Edit(tokens[0].SpanStart, tokens[rule.Parts.Length - 1].Span.End, rule.Replacement));
            }
        }

        return edits;
    }

    // A dotted name is handled once, from its outermost node.
    private static bool IsChainTop(SyntaxNode node)
    {
        if (node is not (QualifiedNameSyntax or IdentifierNameSyntax or MemberAccessExpressionSyntax or AliasQualifiedNameSyntax))
        {
            return false;
        }

        return node.Parent switch
        {
            QualifiedNameSyntax => false,
            MemberAccessExpressionSyntax m => m.Kind() != SyntaxKind.SimpleMemberAccessExpression,
            AliasQualifiedNameSyntax => false,
            _ => true,
        };
    }

    private static bool Matches(string[] parts, List<SyntaxToken> tokens, SyntaxNode top)
    {
        if (tokens.Count < parts.Length)
        {
            return false;
        }

        // A single identifier is a namespace only where a namespace is expected; elsewhere it could be a variable.
        if (tokens.Count == 1 && top.Parent is not (BaseNamespaceDeclarationSyntax or UsingDirectiveSyntax))
        {
            return false;
        }

        for (var i = 0; i < parts.Length; i++)
        {
            if (!string.Equals(tokens[i].ValueText, parts[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // Only pure chains of names (a.b.c, global::a.b) are flattened; a call or index in the middle makes it something else.
    private static bool TryFlatten(SyntaxNode node, List<SyntaxToken> tokens)
    {
        switch (node)
        {
            case IdentifierNameSyntax id:
                tokens.Add(id.Identifier);
                return true;
            case GenericNameSyntax generic:
                tokens.Add(generic.Identifier);
                return true;
            case QualifiedNameSyntax qualified:
                return TryFlatten(qualified.Left, tokens) && TryFlatten(qualified.Right, tokens);
            case AliasQualifiedNameSyntax alias:
                return TryFlatten(alias.Name, tokens);
            case MemberAccessExpressionSyntax access when access.Kind() == SyntaxKind.SimpleMemberAccessExpression:
                return TryFlatten(access.Expression, tokens) && TryFlatten(access.Name, tokens);
            default:
                return false;
        }
    }

    private static List<Edit> FindIdentifierEdits(SyntaxTree tree, Dictionary<string, string> rules)
    {
        var edits = new List<Edit>();
        foreach (var token in tree.GetRoot().DescendantTokens(descendIntoTrivia: true))
        {
            if (token.IsKind(SyntaxKind.IdentifierToken) && rules.TryGetValue(token.ValueText, out var replacement))
            {
                edits.Add(new Edit(token.SpanStart, token.Span.End, replacement));
            }
        }

        return edits;
    }

    private static string Apply(string text, List<Edit> edits)
    {
        if (edits.Count == 0)
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text);
        foreach (var edit in edits.OrderByDescending(e => e.Start))
        {
            builder.Remove(edit.Start, edit.End - edit.Start).Insert(edit.Start, edit.Text);
        }

        return builder.ToString();
    }
}
