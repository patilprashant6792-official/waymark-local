using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Waymark.Modules.Code.Indexing;

/// <summary>Reduces a written type to the simple name the code index compares: no namespace, no type arguments, no nullable mark.</summary>
internal static class TypeNames
{
    // These say nothing about which type a value really has, so a call on them is never matched to a type.
    private static readonly HashSet<string> NotATypeName = new(StringComparer.Ordinal) { "var", "dynamic", "object" };

    /// <returns>For example <c>IList</c> for <c>System.Collections.Generic.IList&lt;int&gt;?</c>; null for arrays, tuples, pointers and <c>var</c>.</returns>
    public static string? Simple(TypeSyntax? type)
    {
        var name = type switch
        {
            NullableTypeSyntax nullable => Simple(nullable.ElementType),
            RefTypeSyntax reference => Simple(reference.Type),
            ScopedTypeSyntax scoped => Simple(scoped.Type),
            QualifiedNameSyntax qualified => Simple(qualified.Right),
            AliasQualifiedNameSyntax aliased => Simple(aliased.Name),
            GenericNameSyntax generic => generic.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            PredefinedTypeSyntax predefined => predefined.Keyword.Text,
            _ => null,
        };

        return name is null || NotATypeName.Contains(name) ? null : name;
    }
}
