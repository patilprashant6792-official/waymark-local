using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Waymark.Core.Code;

namespace Waymark.Modules.Code.Indexing;

internal sealed record ExtractionResult(
    IReadOnlyList<string> Usings,
    IReadOnlyList<CodeTypeEntry> Types,
    IReadOnlyList<CodeMemberEntry> Members,
    IReadOnlyList<CodeCallSiteEntry> CallSites,
    bool HasTopLevelStatements,
    bool Truncated);

/// <summary>
/// Walks the declarations of one parsed file and collects types, members and call sites. Only declarations are
/// walked recursively (nesting is shallow); expressions are scanned with the iterative DescendantNodes, so a very
/// deep expression cannot overflow the stack. One instance per file.
/// </summary>
internal sealed class SyntaxExtractor(SourceText text, CodeIndexOptions options, CancellationToken ct)
{
    private const string NameOfCall = "nameof";

    private readonly List<CodeTypeEntry> _types = [];
    private readonly List<CodeMemberEntry> _members = [];
    private readonly List<CodeCallSiteEntry> _calls = [];
    private readonly ReceiverResolver _receivers = new();
    private bool _hasTopLevelStatements;
    private bool _truncated;

    private readonly record struct TypeContext(int Index, string Name, bool IsInterface);

    private readonly record struct MemberShape(CodeMemberKind Kind, string Name, string Signature, string? ReturnType);

    public ExtractionResult Extract(CompilationUnitSyntax root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var usings = root
            .DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<UsingDirectiveSyntax>()
            .Select(FormatUsing)
            .Where(u => u.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Walk(root.Members, null, null);
        return new ExtractionResult(usings, _types, _members, _calls, _hasTopLevelStatements, _truncated);
    }

    private static string FormatUsing(UsingDirectiveSyntax directive)
    {
        var target = directive.NamespaceOrType.ToString();
        var text = directive.Alias is null ? target : $"{directive.Alias.Name} = {target}";
        return directive.StaticKeyword.IsKind(SyntaxKind.None) ? text : $"static {text}";
    }

    private void Walk(SyntaxList<MemberDeclarationSyntax> members, string? ns, string? outerFullName)
    {
        foreach (var member in members)
        {
            ct.ThrowIfCancellationRequested();
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax declaration:
                    var name = declaration.Name.ToString();
                    Walk(declaration.Members, ns is null ? name : $"{ns}.{name}", null);
                    break;
                case BaseTypeDeclarationSyntax type:
                    AddType(type, ns, outerFullName);
                    break;
                case DelegateDeclarationSyntax delegateType:
                    AddDelegate(delegateType, ns, outerFullName);
                    break;
                case GlobalStatementSyntax statement:
                    _hasTopLevelStatements = true;
                    AddCalls(statement, null, null);
                    break;
            }
        }
    }

    private void AddType(BaseTypeDeclarationSyntax type, string? ns, string? outerFullName)
    {
        var name = type.Identifier.Text;
        if (name.Length == 0)
        {
            return;
        }

        var fullName = FullName(name, ns, outerFullName);
        var typeIndex = _types.Count;
        var (start, end) = LineRange(type.Span);
        var baseList = type.BaseList is null
            ? null
            : DeclarationText.Squash(string.Join(", ", type.BaseList.Types.Select(t => t.Type.ToString())), DeclarationText.MaxSignatureChars);

        _types.Add(new CodeTypeEntry(
            KindOf(type),
            name,
            fullName,
            ns,
            baseList,
            DeclarationText.Modifiers(type.Modifiers, addPublic: false),
            DeclarationText.Attributes(type.AttributeLists),
            DocCommentReader.ReadSummary(type),
            start,
            end));

        switch (type)
        {
            case EnumDeclarationSyntax enumType:
                AddEnumMembers(enumType, typeIndex);
                break;
            case TypeDeclarationSyntax declaration:
                _receivers.PushType(declaration);
                try
                {
                    AddTypeMembers(declaration, new TypeContext(typeIndex, name, type.IsKind(SyntaxKind.InterfaceDeclaration)), fullName, ns);
                }
                finally
                {
                    _receivers.PopType();
                }
                break;
        }
    }

    private void AddDelegate(DelegateDeclarationSyntax delegateType, string? ns, string? outerFullName)
    {
        var name = delegateType.Identifier.Text;
        if (name.Length == 0)
        {
            return;
        }

        var typeIndex = _types.Count;
        var (start, end) = LineRange(delegateType.Span);
        _types.Add(new CodeTypeEntry(
            CodeTypeKind.Delegate,
            name,
            FullName(name, ns, outerFullName),
            ns,
            null,
            DeclarationText.Modifiers(delegateType.Modifiers, addPublic: false),
            DeclarationText.Attributes(delegateType.AttributeLists),
            DocCommentReader.ReadSummary(delegateType),
            start,
            end));

        var returnType = delegateType.ReturnType.ToString();
        var shape = new MemberShape(
            CodeMemberKind.Method,
            "Invoke",
            $"{returnType} Invoke{DeclarationText.Parameters(delegateType.ParameterList)}",
            returnType);
        AddMember(new TypeContext(typeIndex, name, false), shape, delegateType, null, default, default, forcePublic: true);
    }

    private void AddEnumMembers(EnumDeclarationSyntax enumType, int typeIndex)
    {
        var context = new TypeContext(typeIndex, enumType.Identifier.Text, false);
        foreach (var member in enumType.Members)
        {
            ct.ThrowIfCancellationRequested();
            var name = member.Identifier.Text;
            if (name.Length == 0)
            {
                continue;
            }

            var shape = new MemberShape(CodeMemberKind.EnumMember, name, name + (member.EqualsValue is null ? string.Empty : " " + member.EqualsValue), null);
            if (!AddMember(context, shape, member, null, default, member.AttributeLists, forcePublic: true))
            {
                return;
            }
        }
    }

    private void AddTypeMembers(TypeDeclarationSyntax type, TypeContext context, string fullName, string? ns)
    {
        if (type.ParameterList is not null)
        {
            var shape = new MemberShape(CodeMemberKind.Constructor, context.Name, context.Name + DeclarationText.Parameters(type.ParameterList), null);
            if (!AddMember(context, shape, type.ParameterList, null, default, default, forcePublic: false))
            {
                return;
            }
        }

        foreach (var member in type.Members)
        {
            ct.ThrowIfCancellationRequested();
            switch (member)
            {
                case BaseTypeDeclarationSyntax nested:
                    AddType(nested, ns, fullName);
                    break;
                case DelegateDeclarationSyntax nestedDelegate:
                    AddDelegate(nestedDelegate, ns, fullName);
                    break;
                default:
                    if (!AddMemberDeclaration(member, context))
                    {
                        return;
                    }

                    break;
            }
        }
    }

    /// <returns>False when the per-file member limit was reached and the rest of the type is left out.</returns>
    private bool AddMemberDeclaration(MemberDeclarationSyntax member, TypeContext context)
    {
        switch (member)
        {
            case MethodDeclarationSyntax method:
                return AddMember(context, new MemberShape(
                    CodeMemberKind.Method,
                    method.Identifier.Text,
                    $"{method.ReturnType} {method.ExplicitInterfaceSpecifier}{method.Identifier.Text}{DeclarationText.TypeParameters(method.TypeParameterList)}{DeclarationText.Parameters(method.ParameterList)}",
                    method.ReturnType.ToString()), method, method, method.Modifiers, method.AttributeLists);
            case ConstructorDeclarationSyntax constructor:
                return AddMember(context, new MemberShape(
                    CodeMemberKind.Constructor,
                    constructor.Identifier.Text,
                    constructor.Identifier.Text + DeclarationText.Parameters(constructor.ParameterList),
                    null), constructor, constructor, constructor.Modifiers, constructor.AttributeLists);
            case DestructorDeclarationSyntax destructor:
                return AddMember(context, new MemberShape(
                    CodeMemberKind.Method,
                    "~" + destructor.Identifier.Text,
                    "~" + destructor.Identifier.Text + "()",
                    null), destructor, destructor, destructor.Modifiers, destructor.AttributeLists);
            case PropertyDeclarationSyntax property:
                return AddMember(context, new MemberShape(
                    CodeMemberKind.Property,
                    property.Identifier.Text,
                    $"{property.Type} {property.ExplicitInterfaceSpecifier}{property.Identifier.Text}{DeclarationText.Accessors(property.AccessorList, property.ExpressionBody is not null)}",
                    property.Type.ToString()), property, property, property.Modifiers, property.AttributeLists);
            case IndexerDeclarationSyntax indexer:
                return AddMember(context, new MemberShape(
                    CodeMemberKind.Indexer,
                    "this",
                    $"{indexer.Type} {indexer.ExplicitInterfaceSpecifier}this{DeclarationText.BracketedParameters(indexer.ParameterList)}{DeclarationText.Accessors(indexer.AccessorList, indexer.ExpressionBody is not null)}",
                    indexer.Type.ToString()), indexer, indexer, indexer.Modifiers, indexer.AttributeLists);
            case EventDeclarationSyntax eventDeclaration:
                return AddMember(context, new MemberShape(
                    CodeMemberKind.Event,
                    eventDeclaration.Identifier.Text,
                    $"event {eventDeclaration.Type} {eventDeclaration.ExplicitInterfaceSpecifier}{eventDeclaration.Identifier.Text}",
                    eventDeclaration.Type.ToString()), eventDeclaration, eventDeclaration, eventDeclaration.Modifiers, eventDeclaration.AttributeLists);
            case OperatorDeclarationSyntax op:
                return AddMember(context, new MemberShape(
                    CodeMemberKind.Operator,
                    $"operator {op.OperatorToken.Text}",
                    $"{op.ReturnType} operator {op.OperatorToken.Text}{DeclarationText.Parameters(op.ParameterList)}",
                    op.ReturnType.ToString()), op, op, op.Modifiers, op.AttributeLists);
            case ConversionOperatorDeclarationSyntax conversion:
                return AddMember(context, new MemberShape(
                    CodeMemberKind.Operator,
                    $"{conversion.ImplicitOrExplicitKeyword.Text} operator {conversion.Type}",
                    $"{conversion.ImplicitOrExplicitKeyword.Text} operator {conversion.Type}{DeclarationText.Parameters(conversion.ParameterList)}",
                    conversion.Type.ToString()), conversion, conversion, conversion.Modifiers, conversion.AttributeLists);
            case FieldDeclarationSyntax field:
                return AddVariables(context, field, field.Declaration, field.Modifiers, field.AttributeLists, isEvent: false);
            case EventFieldDeclarationSyntax eventField:
                return AddVariables(context, eventField, eventField.Declaration, eventField.Modifiers, eventField.AttributeLists, isEvent: true);
            default:
                return true;
        }
    }

    /// <summary>One entry per declared variable; calls in an initializer belong to the variable that has it.</summary>
    private bool AddVariables(
        TypeContext context,
        MemberDeclarationSyntax declaration,
        VariableDeclarationSyntax variables,
        SyntaxTokenList modifiers,
        SyntaxList<AttributeListSyntax> attributes,
        bool isEvent)
    {
        var type = variables.Type.ToString();
        var isConst = modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword));
        foreach (var variable in variables.Variables)
        {
            var name = variable.Identifier.Text;
            if (name.Length == 0)
            {
                continue;
            }

            var signature = isEvent ? $"event {type} {name}" : $"{type} {name}";
            if (isConst && variable.Initializer is not null)
            {
                signature += " " + variable.Initializer;
            }

            var shape = new MemberShape(isEvent ? CodeMemberKind.Event : CodeMemberKind.Field, name, signature, type);
            if (!AddMember(context, shape, declaration, variable, modifiers, attributes))
            {
                return false;
            }
        }

        return true;
    }

    private bool AddMember(
        TypeContext context,
        MemberShape shape,
        SyntaxNode node,
        SyntaxNode? callScope,
        SyntaxTokenList modifiers,
        SyntaxList<AttributeListSyntax> attributes,
        bool forcePublic = false)
    {
        if (_members.Count >= options.MaxMembersPerFile)
        {
            _truncated = true;
            return false;
        }

        var addPublic = forcePublic || (context.IsInterface && !DeclarationText.HasAccessModifier(modifiers));
        var (start, end) = LineRange(node.Span);
        _members.Add(new CodeMemberEntry(
            context.Index,
            shape.Kind,
            shape.Name,
            DeclarationText.Squash(shape.Signature, DeclarationText.MaxSignatureChars),
            shape.ReturnType is null ? null : DeclarationText.Squash(shape.ReturnType, DeclarationText.MaxSignatureChars),
            DeclarationText.Modifiers(modifiers, addPublic),
            DeclarationText.Attributes(attributes),
            DocCommentReader.ReadSummary(node),
            start,
            end));

        AddCalls(callScope, context.Name, shape.Name);
        return true;
    }

    private void AddCalls(SyntaxNode? scope, string? callerType, string? callerMember)
    {
        if (scope is null)
        {
            return;
        }

        _receivers.BeginScope(scope);
        foreach (var node in scope.DescendantNodes())
        {
            if (node is not InvocationExpressionSyntax invocation
                || !TryReadCallee(invocation, out var callee, out var receiver, out var receiverExpression, out var qualified))
            {
                continue;
            }

            if (_calls.Count >= options.MaxCallSitesPerFile)
            {
                _truncated = true;
                return;
            }

            var (receiverType, guessed) = qualified
                ? _receivers.Resolve(receiverExpression, callerType)
                : ReceiverResolver.ResolveUnqualified(callerType);
            _calls.Add(new CodeCallSiteEntry(
                callerType,
                callerMember,
                callee,
                receiver,
                text.Lines.GetLinePosition(invocation.SpanStart).Line + 1,
                receiverType,
                guessed));
        }
    }

    /// <param name="receiverExpression">The expression the method is called on; null for an unqualified call and when it cannot be read.</param>
    /// <param name="qualified">False for an unqualified call (<c>Run()</c>), which goes to the enclosing type.</param>
    private static bool TryReadCallee(
        InvocationExpressionSyntax invocation,
        out string callee,
        out string? receiver,
        out ExpressionSyntax? receiverExpression,
        out bool qualified)
    {
        receiver = null;
        receiverExpression = null;
        qualified = false;
        switch (invocation.Expression)
        {
            case MemberAccessExpressionSyntax access:
                callee = access.Name.Identifier.Text;
                receiver = ReceiverOf(access.Expression);
                receiverExpression = access.Expression;
                qualified = true;
                break;
            case MemberBindingExpressionSyntax binding:
                callee = binding.Name.Identifier.Text;
                qualified = true;
                if (invocation.Parent is ConditionalAccessExpressionSyntax conditional && ReferenceEquals(conditional.WhenNotNull, invocation))
                {
                    receiver = ReceiverOf(conditional.Expression);
                    receiverExpression = conditional.Expression;
                }

                break;
            case SimpleNameSyntax name:
                callee = name is IdentifierNameSyntax && name.Identifier.Text == NameOfCall ? string.Empty : name.Identifier.Text;
                break;
            default:
                callee = string.Empty;
                break;
        }

        return callee.Length > 0;
    }

    /// <summary>The leftmost name of a receiver chain (<c>_service</c> for <c>_service.Items.Add()</c>); null when the receiver is not a plain name.</summary>
    private static string? ReceiverOf(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case IdentifierNameSyntax identifier:
                    return identifier.Identifier.Text;
                case GenericNameSyntax generic:
                    return generic.Identifier.Text;
                case ThisExpressionSyntax:
                    return "this";
                case BaseExpressionSyntax:
                    return "base";
                case PredefinedTypeSyntax predefined:
                    return predefined.Keyword.Text;
                case MemberAccessExpressionSyntax access:
                    expression = access.Expression;
                    continue;
                default:
                    return null;
            }
        }
    }

    private (int Start, int End) LineRange(TextSpan span)
    {
        var lines = text.Lines.GetLinePositionSpan(span);
        return (lines.Start.Line + 1, lines.End.Line + 1);
    }

    private static string FullName(string name, string? ns, string? outerFullName)
    {
        if (outerFullName is not null)
        {
            return $"{outerFullName}.{name}";
        }

        return ns is null ? name : $"{ns}.{name}";
    }

    private static CodeTypeKind KindOf(BaseTypeDeclarationSyntax type) => type.Kind() switch
    {
        SyntaxKind.InterfaceDeclaration => CodeTypeKind.Interface,
        SyntaxKind.StructDeclaration => CodeTypeKind.Struct,
        SyntaxKind.RecordDeclaration => CodeTypeKind.Record,
        SyntaxKind.RecordStructDeclaration => CodeTypeKind.RecordStruct,
        SyntaxKind.EnumDeclaration => CodeTypeKind.Enum,
        _ => CodeTypeKind.Class
    };
}
