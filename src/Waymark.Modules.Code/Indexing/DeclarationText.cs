using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Waymark.Modules.Code.Indexing;

/// <summary>Turns declaration pieces into the short single-line texts kept in the index.</summary>
internal static class DeclarationText
{
    public const int MaxSignatureChars = 1000;
    public const int MaxAttributeChars = 500;
    private const int MaxParameterChars = 200;
    private const string Ellipsis = "...";

    /// <summary>Collapses every run of white space to one space and cuts the text to <paramref name="maxChars"/>.</summary>
    public static string Squash(string text, int maxChars)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= maxChars
            ? collapsed
            : string.Concat(collapsed.AsSpan(0, maxChars - Ellipsis.Length), Ellipsis);
    }

    public static bool HasAccessModifier(SyntaxTokenList modifiers) =>
        modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)
            || m.IsKind(SyntaxKind.PrivateKeyword)
            || m.IsKind(SyntaxKind.ProtectedKeyword)
            || m.IsKind(SyntaxKind.InternalKeyword));

    /// <summary>The modifiers as written, space separated. <paramref name="addPublic"/> puts "public" in front (interface and enum members are public without saying so).</summary>
    public static string? Modifiers(SyntaxTokenList modifiers, bool addPublic)
    {
        var words = modifiers.Select(m => m.Text).ToList();
        if (addPublic)
        {
            words.Insert(0, "public");
        }

        return words.Count == 0 ? null : string.Join(' ', words);
    }

    /// <summary>One attribute per line, without the brackets, for example <c>McpServerTool(Name = "x")</c>.</summary>
    public static string? Attributes(SyntaxList<AttributeListSyntax> lists)
    {
        var items = lists
            .SelectMany(list => list.Attributes)
            .Select(attribute => Squash(attribute.ToString(), MaxAttributeChars))
            .ToList();
        return items.Count == 0 ? null : string.Join('\n', items);
    }

    public static string Parameters(ParameterListSyntax? list) =>
        list is null ? "()" : "(" + JoinParameters(list.Parameters) + ")";

    public static string BracketedParameters(BracketedParameterListSyntax? list) =>
        list is null ? "[]" : "[" + JoinParameters(list.Parameters) + "]";

    public static string TypeParameters(TypeParameterListSyntax? list) =>
        list is null ? string.Empty : Squash(list.ToString(), MaxParameterChars);

    /// <summary>For example <c> { get; private set; }</c>; an expression-bodied property shows as read-only.</summary>
    public static string Accessors(AccessorListSyntax? list, bool isExpressionBodied)
    {
        if (list is null)
        {
            return isExpressionBodied ? " { get; }" : string.Empty;
        }

        var parts = list.Accessors.Select(accessor =>
            string.Join(' ', accessor.Modifiers.Select(m => m.Text).Append(accessor.Keyword.Text)) + ";");
        return " { " + string.Join(' ', parts) + " }";
    }

    private static string JoinParameters(SeparatedSyntaxList<ParameterSyntax> parameters) =>
        string.Join(", ", parameters.Select(p => Squash(p.ToString(), MaxParameterChars)));
}
