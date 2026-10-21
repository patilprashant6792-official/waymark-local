using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Waymark.Modules.Code.Indexing;

/// <summary>Reads the text of the summary element of an XML documentation comment.</summary>
internal static class DocCommentReader
{
    public const int MaxSummaryChars = 500;
    private const int MaxNesting = 16;
    private const string SummaryElement = "summary";

    /// <summary>The summary as one line of plain text; null when the node has no documentation comment or no summary in it.</summary>
    public static string? ReadSummary(SyntaxNode node)
    {
        foreach (var trivia in node.GetLeadingTrivia())
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                && !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                continue;
            }

            if (trivia.GetStructure() is not DocumentationCommentTriviaSyntax comment)
            {
                continue;
            }

            foreach (var element in comment.Content.OfType<XmlElementSyntax>())
            {
                if (element.StartTag.Name.LocalName.ValueText != SummaryElement)
                {
                    continue;
                }

                var builder = new StringBuilder();
                Append(element.Content, builder, 0);
                var summary = DeclarationText.Squash(builder.ToString(), MaxSummaryChars);
                return summary.Length == 0 ? null : summary;
            }
        }

        return null;
    }

    private static void Append(SyntaxList<XmlNodeSyntax> nodes, StringBuilder builder, int depth)
    {
        if (depth > MaxNesting)
        {
            return;
        }

        foreach (var node in nodes)
        {
            if (builder.Length > MaxSummaryChars * 2)
            {
                return;
            }

            switch (node)
            {
                case XmlTextSyntax text:
                    foreach (var token in text.TextTokens)
                    {
                        builder.Append(token.IsKind(SyntaxKind.XmlTextLiteralNewLineToken) ? " " : token.Text);
                    }

                    break;
                case XmlEmptyElementSyntax empty:
                    AppendReference(empty.Attributes, builder);
                    break;
                case XmlElementSyntax element:
                    Append(element.Content, builder, depth + 1);
                    break;
            }
        }
    }

    /// <summary>For an element such as &lt;see cref="X"/&gt; the referenced name stands in for the element.</summary>
    private static void AppendReference(SyntaxList<XmlAttributeSyntax> attributes, StringBuilder builder)
    {
        foreach (var attribute in attributes)
        {
            switch (attribute)
            {
                case XmlCrefAttributeSyntax cref:
                    builder.Append(' ').Append(cref.Cref.ToString()).Append(' ');
                    return;
                case XmlNameAttributeSyntax name:
                    builder.Append(' ').Append(name.Identifier.Identifier.ValueText).Append(' ');
                    return;
                case XmlTextAttributeSyntax value:
                    builder.Append(' ').Append(string.Concat(value.TextTokens.Select(t => t.Text))).Append(' ');
                    return;
            }
        }
    }
}
