using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Waymark.Modules.Code.Indexing;

/// <summary>
/// Finds the declared type of a call's receiver from the file alone, without a compilation: parameters, locals,
/// fields, properties and primary-constructor parameters of the enclosing types, <c>new T()</c>, <c>this</c> and
/// capitalised names that no declaration explains (static types). Anything it cannot tell stays null, because a wrong
/// type would hide real callers while an unknown one only keeps a call in the list.
/// A name declared twice with different types in one member (shadowing) counts as unknown.
/// One instance per file; the extractor tells it when it enters and leaves a type and a member.
/// </summary>
internal sealed class ReceiverResolver
{
    private readonly Stack<TypeScope> _types = new();
    private readonly HashSet<string> _methodTypeParameters = new(StringComparer.Ordinal);
    private Dictionary<string, string?> _locals = new(StringComparer.Ordinal);

    private sealed record TypeScope(Dictionary<string, string?> Declared, HashSet<string> TypeParameters);

    public void PushType(TypeDeclarationSyntax type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var declared = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (type.ParameterList is { } primary)
        {
            foreach (var parameter in primary.Parameters)
            {
                Declare(declared, parameter.Identifier.Text, TypeNames.Simple(parameter.Type));
            }
        }

        foreach (var member in type.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax field:
                    DeclareVariables(declared, field.Declaration);
                    break;
                case EventFieldDeclarationSyntax eventField:
                    DeclareVariables(declared, eventField.Declaration);
                    break;
                case PropertyDeclarationSyntax property:
                    Declare(declared, property.Identifier.Text, TypeNames.Simple(property.Type));
                    break;
            }
        }

        var typeParameters = new HashSet<string>(StringComparer.Ordinal);
        AddTypeParameters(typeParameters, type.TypeParameterList);
        _types.Push(new TypeScope(declared, typeParameters));
    }

    public void PopType() => _types.Pop();

    /// <summary>Collects the names declared inside one member (or one field initializer) before its calls are resolved.</summary>
    public void BeginScope(SyntaxNode scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        _locals = new Dictionary<string, string?>(StringComparer.Ordinal);
        _methodTypeParameters.Clear();
        if (scope is MethodDeclarationSyntax method)
        {
            AddTypeParameters(_methodTypeParameters, method.TypeParameterList);
        }

        foreach (var node in scope.DescendantNodes())
        {
            switch (node)
            {
                case ParameterSyntax parameter:
                    Declare(_locals, parameter.Identifier.Text, TypeNames.Simple(parameter.Type));
                    break;
                case VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } variable:
                    Declare(_locals, variable.Identifier.Text, DeclaredType(declaration.Type, variable.Initializer?.Value));
                    break;
                case ForEachStatementSyntax loop:
                    Declare(_locals, loop.Identifier.Text, DeclaredType(loop.Type, null));
                    break;
                case CatchDeclarationSyntax handler:
                    Declare(_locals, handler.Identifier.Text, TypeNames.Simple(handler.Type));
                    break;
                case DeclarationPatternSyntax { Designation: SingleVariableDesignationSyntax name } pattern:
                    Declare(_locals, name.Identifier.Text, TypeNames.Simple(pattern.Type));
                    break;
                case DeclarationExpressionSyntax { Designation: SingleVariableDesignationSyntax name } expression:
                    Declare(_locals, name.Identifier.Text, DeclaredType(expression.Type, null));
                    break;
                case SingleVariableDesignationSyntax { Parent: not (DeclarationPatternSyntax or DeclarationExpressionSyntax) } designation:
                    Declare(_locals, designation.Identifier.Text, null);
                    break;
                case FromClauseSyntax fromClause:
                    Declare(_locals, fromClause.Identifier.Text, null);
                    break;
                case LetClauseSyntax letClause:
                    Declare(_locals, letClause.Identifier.Text, null);
                    break;
                case JoinClauseSyntax joinClause:
                    Declare(_locals, joinClause.Identifier.Text, null);
                    break;
                case JoinIntoClauseSyntax joinInto:
                    Declare(_locals, joinInto.Identifier.Text, null);
                    break;
                case QueryContinuationSyntax continuation:
                    Declare(_locals, continuation.Identifier.Text, null);
                    break;
                case LocalFunctionStatementSyntax function:
                    AddTypeParameters(_methodTypeParameters, function.TypeParameterList);
                    break;
            }
        }
    }

    /// <summary>A call without a receiver goes to the enclosing type (or one of its base types).</summary>
    public static (string? Type, bool Guessed) ResolveUnqualified(string? enclosingType) => (enclosingType, false);

    /// <summary>The type of the receiver expression; <c>Guessed</c> is true when it is only a capitalised name nothing declares.</summary>
    public (string? Type, bool Guessed) Resolve(ExpressionSyntax? receiver, string? enclosingType)
    {
        switch (receiver)
        {
            case ThisExpressionSyntax:
                return (enclosingType, false);
            case PredefinedTypeSyntax predefined:
                return Known(predefined.Keyword.Text);
            case ObjectCreationExpressionSyntax creation:
                return Known(TypeNames.Simple(creation.Type));
            case IdentifierNameSyntax identifier:
                return ResolveName(identifier.Identifier.Text);
            case GenericNameSyntax generic:
                return (generic.Identifier.Text, true);
            case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } access:
                return _types.TryPeek(out var scope) && scope.Declared.TryGetValue(access.Name.Identifier.Text, out var member)
                    ? Known(member)
                    : (null, false);
            default:
                return (null, false);
        }
    }

    private (string? Type, bool Guessed) ResolveName(string name)
    {
        if (_locals.TryGetValue(name, out var local))
        {
            return Known(local);
        }

        foreach (var scope in _types)
        {
            if (scope.Declared.TryGetValue(name, out var declared))
            {
                return Known(declared);
            }
        }

        return name.Length > 0 && char.IsUpper(name[0]) ? (name, true) : (null, false);
    }

    // A type parameter says nothing about the real type (it may be constrained to the type being searched), so it stays unknown.
    private (string? Type, bool Guessed) Known(string? type) =>
        type is null || _methodTypeParameters.Contains(type) || _types.Any(scope => scope.TypeParameters.Contains(type))
            ? (null, false)
            : (type, false);

    private static string? DeclaredType(TypeSyntax type, ExpressionSyntax? initializer) =>
        type.IsVar ? InferFromInitializer(initializer) : TypeNames.Simple(type);

    private static string? InferFromInitializer(ExpressionSyntax? initializer) => initializer switch
    {
        ObjectCreationExpressionSyntax creation => TypeNames.Simple(creation.Type),
        CastExpressionSyntax cast => TypeNames.Simple(cast.Type),
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AsExpression) => binary.Right is TypeSyntax type ? TypeNames.Simple(type) : null,
        ParenthesizedExpressionSyntax parenthesized => InferFromInitializer(parenthesized.Expression),
        _ => null,
    };

    private static void DeclareVariables(Dictionary<string, string?> bag, VariableDeclarationSyntax declaration)
    {
        var type = TypeNames.Simple(declaration.Type);
        foreach (var variable in declaration.Variables)
        {
            Declare(bag, variable.Identifier.Text, type);
        }
    }

    private static void Declare(Dictionary<string, string?> bag, string name, string? type)
    {
        if (name.Length == 0)
        {
            return;
        }

        if (!bag.TryAdd(name, type) && bag[name] != type)
        {
            bag[name] = null;
        }
    }

    private static void AddTypeParameters(HashSet<string> names, TypeParameterListSyntax? list)
    {
        if (list is null)
        {
            return;
        }

        foreach (var parameter in list.Parameters)
        {
            names.Add(parameter.Identifier.Text);
        }
    }
}
