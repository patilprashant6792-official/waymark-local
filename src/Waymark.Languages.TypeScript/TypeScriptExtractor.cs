using TreeSitter;
using Waymark.Core.Code;

namespace Waymark.Languages.TypeScript;

internal sealed record TypeScriptExtraction(
    IReadOnlyList<string> Imports,
    IReadOnlyList<CodeTypeEntry> Types,
    IReadOnlyList<CodeMemberEntry> Members,
    IReadOnlyList<CodeCallSiteEntry> CallSites,
    bool Truncated);

/// <summary>
/// Reads the declarations of one parsed TypeScript or JavaScript file into types, members and call sites. Statements are walked recursively (nesting is
/// shallow); expressions are scanned with an explicit stack, so a very deep expression cannot overflow the stack. One instance per file.
/// </summary>
/// <remarks>
/// Mapping: a class is a Class, an interface an Interface, an enum an Enum, a type alias a TypeAlias, a namespace or <c>declare module</c> a Namespace (its members belong to it).
/// A function declaration, a function signature and a const or let that holds an arrow or function expression are Functions; any other top-level const, let or var is a Variable.
/// In a class: a method, a constructor, a get accessor (Property), a field (a field that holds a function is a Method), a constructor parameter with an access modifier (Field).
/// A top-level name is public when it is exported; in a file with no import or export (a script, CommonJS, a .d.ts) everything is public.
/// </remarks>
internal sealed class TypeScriptExtractor(string moduleName, int lineCount, CodeIndexLimits limits, CancellationToken ct)
{
    private const int MaxSignatureChars = 1000;
    private const int MaxAttributeChars = 500;
    private const int MaxImportChars = 300;
    private const int MaxSummaryChars = 500;
    private const int MaxValueChars = 80;
    private const int MaxValueRows = 4;
    private const int MaxImports = 1000;
    private const int MaxNesting = 48;
    private const int MaxChainDepth = 64;
    private const string Elided = "...";

    private static readonly HashSet<string> FunctionValues = new(StringComparer.Ordinal) { "arrow_function", "function_expression", "function", "generator_function" };

    private readonly List<string> _imports = [];
    private readonly HashSet<string> _seenImports = new(StringComparer.Ordinal);
    private readonly List<CodeTypeEntry> _types = [];
    private readonly List<CodeMemberEntry> _members = [];
    private readonly List<CodeCallSiteEntry> _calls = [];
    private bool _topLevelPublic;
    private readonly HashSet<string> _namedExports = new(StringComparer.Ordinal);
    private bool _membersFull;
    private bool _callsFull;

    private sealed record TypeScope(int Index, string Name, string FullName, bool IsNamespace);

    private sealed record Context(TypeScope? Scope, bool Exported, bool IsDefault, string? Doc, IReadOnlyList<string> Decorators);

    public TypeScriptExtraction Extract(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _topLevelPublic = !root.NamedChildren.Any(n => n.Type is "export_statement" or "import_statement");

        // export { A, B as C };  names a local declaration as exported without the export keyword on it.
        foreach (var statement in root.NamedChildren.Where(n => n.Type == "export_statement" && n.GetChildForField("source") is null))
        {
            foreach (var clause in statement.NamedChildren.Where(c => c.Type == "export_clause"))
            {
                foreach (var specifier in clause.NamedChildren.Where(c => c.Type == "export_specifier"))
                {
                    var local = specifier.GetChildForField("name")?.Text;
                    if (!string.IsNullOrEmpty(local))
                    {
                        _namedExports.Add(local);
                    }
                }
            }
        }
        WalkStatements(root.NamedChildren, null, 0);
        return new TypeScriptExtraction(_imports, _types, _members, _calls, _membersFull || _callsFull);
    }

    private void WalkStatements(IReadOnlyList<Node> statements, TypeScope? scope, int depth)
    {
        string? doc = null;
        var docEndRow = -10;
        foreach (var statement in statements)
        {
            ct.ThrowIfCancellationRequested();
            if (_membersFull)
            {
                return;
            }

            if (statement.Type == "comment")
            {
                var summary = TypeScriptText.JsDocSummary(statement.Text, MaxSummaryChars);
                if (summary is not null)
                {
                    doc = summary;
                    docEndRow = statement.EndPosition.Row;
                }

                continue;
            }

            var attached = doc is not null && statement.StartPosition.Row - docEndRow <= 1 ? doc : null;
            doc = null;
            WalkStatement(statement, new Context(scope, false, false, attached, []), depth);
        }
    }

    private void WalkStatement(Node node, Context context, int depth)
    {
        switch (node.Type)
        {
            case "import_statement":
                AddImport(node);
                break;
            case "export_statement":
                WalkExport(node, context, depth);
                break;
            case "ambient_declaration":
                foreach (var child in node.NamedChildren)
                {
                    if (child.Type != "comment")
                    {
                        WalkStatement(child, context with { Exported = true }, depth);
                    }
                }

                break;
            case "class_declaration":
            case "abstract_class_declaration":
            case "class":
                AddClass(node, node, context, depth);
                break;
            case "interface_declaration":
                AddInterface(node, node, context, depth);
                break;
            case "enum_declaration":
                AddEnum(node, node, context, depth);
                break;
            case "type_alias_declaration":
                AddTypeAlias(node, node, context);
                break;
            case "function_declaration":
            case "generator_function_declaration":
            case "function_signature":
                AddFunction(node, node, context);
                break;
            case "lexical_declaration":
            case "variable_declaration":
                AddVariables(node, node, context);
                break;
            case "internal_module":
            case "module":
                AddNamespace(node, node, context, depth);
                break;
            case "expression_statement":
                if (node.NamedChildren.Count == 1 && node.NamedChildren[0].Type is "internal_module" or "module")
                {
                    AddNamespace(node.NamedChildren[0], node, context, depth);
                }
                else
                {
                    AddCalls(node, context.Scope?.Name, null);
                }

                break;
            case "comment":
                break;
            default:
                AddCalls(node, context.Scope?.Name, null);
                break;
        }
    }

    private void WalkExport(Node node, Context context, int depth)
    {
        var decorators = DecoratorsOf(node);
        var isDefault = node.Children.Any(c => c.Type == "default");
        var declaration = node.GetChildForField("declaration");
        var source = node.GetChildForField("source");

        // export declare class X {}, export declare function f(): void, export declare const c: T: the declaration sits inside an ambient_declaration.
        var ambient = declaration?.Type == "ambient_declaration" ? declaration : node.NamedChildren.FirstOrDefault(c => c.Type == "ambient_declaration");
        if (ambient is not null)
        {
            WalkStatement(ambient, context with { Exported = true, IsDefault = isDefault, Decorators = decorators }, depth);
            return;
        }

        if (declaration is null)
        {
            // export { a } from "x";  export * from "x";  export default someValue;
            if (source is not null)
            {
                AddImport(node);
            }
            else
            {
                AddCalls(node, context.Scope?.Name, null);
            }

            return;
        }

        var inner = context with { Exported = true, IsDefault = isDefault, Decorators = decorators };
        if (declaration.Type == "expression_statement" || declaration.Type == "parenthesized_expression")
        {
            AddCalls(declaration, context.Scope?.Name, null);
            return;
        }

        // The span is the whole export statement, so a decorator written before 'export' belongs to the class.
        switch (declaration.Type)
        {
            case "class_declaration":
            case "abstract_class_declaration":
            case "class":
                AddClass(declaration, node, inner, depth);
                break;
            case "interface_declaration":
                AddInterface(declaration, node, inner, depth);
                break;
            case "enum_declaration":
                AddEnum(declaration, node, inner, depth);
                break;
            case "type_alias_declaration":
                AddTypeAlias(declaration, node, inner);
                break;
            case "function_declaration":
            case "generator_function_declaration":
            case "function_signature":
                AddFunction(declaration, node, inner);
                break;
            case "lexical_declaration":
            case "variable_declaration":
                AddVariables(declaration, node, inner);
                break;
            case "internal_module":
            case "module":
                AddNamespace(declaration, node, inner, depth);
                break;
            default:
                // export default <expression>: an arrow function, a call, an object.
                AddCalls(declaration, context.Scope?.Name, null);
                break;
        }
    }

    private void AddImport(Node node)
    {
        if (_imports.Count >= MaxImports)
        {
            return;
        }

        var text = TypeScriptText.Squash(node.Text, MaxImportChars);
        if (text.Length > 0 && _seenImports.Add(text))
        {
            _imports.Add(text);
        }
    }

    private void AddClass(Node node, Node span, Context context, int depth)
    {
        var name = NameOf(node) ?? (context.IsDefault ? "default" : null);
        if (name is null || depth >= MaxNesting || !CanAddType())
        {
            return;
        }

        var bases = new List<string>();
        foreach (var heritage in node.NamedChildren.Where(c => c.Type == "class_heritage"))
        {
            var clauses = heritage.NamedChildren.Where(c => c.Type is "extends_clause" or "implements_clause").ToList();
            foreach (var clause in clauses.Count > 0 ? clauses.SelectMany(c => c.NamedChildren) : heritage.NamedChildren)
            {
                if (clause.Type is not ("type_arguments" or "comment"))
                {
                    var text = TypeScriptText.Squash(clause.Text, 200);
                    if (text.Length > 0)
                    {
                        bases.Add(text);
                    }
                }
            }
        }

        var modifiers = TopLevelVisibility(context, name);
        if (node.Type == "abstract_class_declaration")
        {
            modifiers += " abstract";
        }

        if (context.IsDefault)
        {
            modifiers += " default";
        }

        var scope = BeginType(CodeTypeKind.Class, name, span, context, bases, modifiers, node);
        var body = node.GetChildForField("body");
        if (body is not null)
        {
            WalkClassBody(body, scope, depth);
        }
    }

    private void AddInterface(Node node, Node span, Context context, int depth)
    {
        var name = NameOf(node);
        if (name is null || depth >= MaxNesting || !CanAddType())
        {
            return;
        }

        var bases = new List<string>();
        foreach (var clause in node.NamedChildren.Where(c => c.Type is "extends_type_clause" or "extends_clause"))
        {
            foreach (var item in clause.NamedChildren)
            {
                if (item.Type is not ("type_arguments" or "comment"))
                {
                    bases.Add(TypeScriptText.Squash(item.Text, 200));
                }
            }
        }

        var scope = BeginType(CodeTypeKind.Interface, name, span, context, bases, TopLevelVisibility(context, name), node);
        var body = node.GetChildForField("body");
        if (body is not null)
        {
            WalkClassBody(body, scope, depth);
        }
    }

    private void AddEnum(Node node, Node span, Context context, int depth)
    {
        var name = NameOf(node);
        if (name is null || depth >= MaxNesting || !CanAddType())
        {
            return;
        }

        var scope = BeginType(CodeTypeKind.Enum, name, span, context, [], TopLevelVisibility(context, name), node);
        var body = node.GetChildForField("body");
        if (body is null)
        {
            return;
        }

        foreach (var member in body.NamedChildren)
        {
            string? memberName;
            string signature;
            if (member.Type == "enum_assignment")
            {
                memberName = MemberName(member.GetChildForField("name"));
                var value = member.GetChildForField("value");
                signature = memberName is null ? string.Empty : value is null ? memberName : $"{memberName} = {ValueText(value)}";
            }
            else if (member.Type is "property_identifier" or "string" or "computed_property_name")
            {
                memberName = MemberName(member);
                signature = memberName ?? string.Empty;
            }
            else
            {
                continue;
            }

            if (memberName is null)
            {
                continue;
            }

            var (start, end) = Lines(member);
            AddMemberEntry(new CodeMemberEntry(scope.Index, CodeMemberKind.EnumMember, memberName, signature, null, "public", null, null, start, end));
        }
    }

    private void AddTypeAlias(Node node, Node span, Context context)
    {
        var name = NameOf(node);
        if (name is null || !CanAddType())
        {
            return;
        }

        BeginType(CodeTypeKind.TypeAlias, name, span, context, [], TopLevelVisibility(context, name), node);
    }

    private void AddNamespace(Node node, Node span, Context context, int depth)
    {
        var nameNode = node.GetChildForField("name");
        var name = nameNode is null ? null : TypeScriptText.Unquote(TypeScriptText.Squash(nameNode.Text, 200));
        if (string.IsNullOrEmpty(name) || depth >= MaxNesting || !CanAddType())
        {
            return;
        }

        var scope = BeginType(CodeTypeKind.Namespace, name, span, context, [], TopLevelVisibility(context, name), node);
        var body = node.GetChildForField("body");
        if (body is not null)
        {
            WalkStatements(body.NamedChildren, scope, depth + 1);
        }
    }

    /// <summary>Adds the type entry and returns the scope its members are added to.</summary>
    private TypeScope BeginType(CodeTypeKind kind, string name, Node span, Context context, IReadOnlyList<string> bases, string modifiers, Node declaration)
    {
        var index = _types.Count;
        var fullName = context.Scope is not null ? $"{context.Scope.FullName}.{name}" : Qualified(name);
        var (start, end) = Lines(span);
        var decorators = context.Decorators.Concat(DecoratorsOf(declaration)).ToList();
        _types.Add(new CodeTypeEntry(
            kind,
            name,
            fullName,
            ModuleOrNull(),
            bases.Count == 0 ? null : TypeScriptText.Squash(string.Join(", ", bases), MaxSignatureChars),
            modifiers,
            JoinLines(decorators),
            context.Doc,
            start,
            end));
        return new TypeScope(index, name, fullName, kind == CodeTypeKind.Namespace);
    }

    private void WalkClassBody(Node body, TypeScope scope, int depth)
    {
        var decorators = new List<string>();
        string? doc = null;
        var docEndRow = -10;
        var seenAccessors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in body.NamedChildren)
        {
            ct.ThrowIfCancellationRequested();
            if (_membersFull)
            {
                return;
            }

            switch (member.Type)
            {
                case "decorator":
                    decorators.Add(DecoratorText(member));
                    continue;
                case "comment":
                    var summary = TypeScriptText.JsDocSummary(member.Text, MaxSummaryChars);
                    if (summary is not null)
                    {
                        doc = summary;
                        docEndRow = member.EndPosition.Row;
                    }

                    continue;
            }

            var attached = doc is not null && member.StartPosition.Row - docEndRow <= 1 ? doc : null;
            AddClassMember(member, scope, decorators, attached, seenAccessors, depth);
            decorators = [];
            doc = null;
        }
    }

    private void AddClassMember(Node member, TypeScope scope, List<string> decorators, string? doc, HashSet<string> seenAccessors, int depth)
    {
        switch (member.Type)
        {
            case "method_definition":
            case "method_signature":
            case "abstract_method_signature":
                AddMethod(member, scope, decorators, doc, seenAccessors);
                break;
            case "public_field_definition":
            case "field_definition":
            case "property_signature":
                AddField(member, scope, decorators, doc);
                break;
            case "index_signature":
                AddMemberEntry(new CodeMemberEntry(
                    scope.Index,
                    CodeMemberKind.Indexer,
                    "[]",
                    TypeScriptText.Squash(member.Text.TrimEnd(';', ','), MaxSignatureChars),
                    null,
                    "public",
                    null,
                    doc,
                    Lines(member).Start,
                    Lines(member).End));
                break;
            case "class_static_block":
                AddCalls(member, scope.Name, "static");
                break;
            default:
                // call and construct signatures of an interface, and anything this reader does not know, are left out.
                break;
        }
    }

    private void AddMethod(Node member, TypeScope scope, List<string> decorators, string? doc, HashSet<string> seenAccessors)
    {
        var name = MemberName(member.GetChildForField("name"));
        if (name is null)
        {
            return;
        }

        var keywords = Keywords(member);
        var isAccessor = keywords.Contains("get") || keywords.Contains("set");
        var isConstructor = name == "constructor" && member.Type == "method_definition";
        var kind = isConstructor ? CodeMemberKind.Constructor : isAccessor ? CodeMemberKind.Property : CodeMemberKind.Method;
        var parameters = member.GetChildForField("parameters");
        var returnType = ReturnTypeText(member);
        var accessor = keywords.Contains("get") ? "get " : keywords.Contains("set") ? "set " : string.Empty;
        var signature = $"{accessor}{name}{TypeParametersText(member)}{ParametersText(parameters)}{(returnType is null ? string.Empty : ": " + returnType)}";
        var (start, end) = Lines(member);
        var memberName = isConstructor ? scope.Name : name;

        // A setter has the name of its getter; the getter already stands for the property.
        var add = !(isAccessor && !seenAccessors.Add(name));
        if (add)
        {
            var added = AddMemberEntry(new CodeMemberEntry(
                scope.Index,
                kind,
                memberName,
                TypeScriptText.Squash(signature, MaxSignatureChars),
                returnType,
                MemberModifiers(member, name, keywords, scope),
                JoinLines(decorators),
                doc,
                start,
                end));
            if (!added)
            {
                return;
            }
        }

        if (isConstructor)
        {
            AddParameterProperties(parameters, scope);
        }

        var body = member.GetChildForField("body");
        if (body is not null)
        {
            AddCalls(body, scope.Name, memberName);
        }
    }

    private void AddField(Node member, TypeScope scope, List<string> decorators, string? doc)
    {
        var name = MemberName(member.GetChildForField("name") ?? member.GetChildForField("property"));
        if (name is null)
        {
            return;
        }

        var keywords = Keywords(member);
        var typeNode = member.GetChildForField("type");
        var typeText = typeNode is null ? null : TrimTypeAnnotation(typeNode.Text);
        var value = member.GetChildForField("value");
        var optional = member.Children.Any(c => c.Type == "?") ? "?" : string.Empty;
        var (start, end) = Lines(member);
        var modifiers = MemberModifiers(member, name, keywords, scope);

        if (value is not null && FunctionValues.Contains(value.Type))
        {
            // An arrow function held in a field is a method in every way that matters to a reader.
            var arrowReturn = ReturnTypeText(value);
            var signature = $"{name}{TypeParametersText(value)}{ParametersText(value.GetChildForField("parameters") ?? value.GetChildForField("parameter"))}{(arrowReturn is null ? string.Empty : ": " + arrowReturn)}";
            if (AddMemberEntry(new CodeMemberEntry(
                scope.Index, CodeMemberKind.Method, name, TypeScriptText.Squash(signature, MaxSignatureChars), arrowReturn, modifiers, JoinLines(decorators), doc, start, end)))
            {
                AddCalls(value, scope.Name, name);
            }

            return;
        }

        var kind = member.Type == "property_signature" ? CodeMemberKind.Property : CodeMemberKind.Field;
        var text = name + optional + (typeText is null ? string.Empty : ": " + typeText);
        if (value is not null)
        {
            text += " = " + ValueText(value);
        }

        if (AddMemberEntry(new CodeMemberEntry(
            scope.Index, kind, name, TypeScriptText.Squash(text, MaxSignatureChars), typeText, modifiers, JoinLines(decorators), doc, start, end)) && value is not null)
        {
            AddCalls(value, scope.Name, name);
        }
    }

    /// <summary><c>constructor(private readonly repo: Repo)</c> declares a field named repo.</summary>
    private void AddParameterProperties(Node? parameters, TypeScope scope)
    {
        if (parameters is null)
        {
            return;
        }

        foreach (var parameter in parameters.NamedChildren.Where(p => p.Type is "required_parameter" or "optional_parameter"))
        {
            var access = parameter.NamedChildren.FirstOrDefault(c => c.Type == "accessibility_modifier")?.Text;
            var isReadonly = parameter.Children.Any(c => c.Type == "readonly");
            if (access is null && !isReadonly)
            {
                continue;
            }

            var pattern = parameter.GetChildForField("pattern");
            var name = pattern is null ? null : TypeScriptText.Squash(pattern.Text, 100);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var typeNode = parameter.GetChildForField("type");
            var typeText = typeNode is null ? null : TrimTypeAnnotation(typeNode.Text);
            var (start, end) = Lines(parameter);
            AddMemberEntry(new CodeMemberEntry(
                scope.Index,
                CodeMemberKind.Field,
                name,
                typeText is null ? name : $"{name}: {typeText}",
                typeText,
                (access ?? "public") + (isReadonly ? " readonly" : string.Empty),
                null,
                null,
                start,
                end));
        }
    }

    private void AddFunction(Node node, Node span, Context context)
    {
        var name = NameOf(node) ?? (context.IsDefault ? "default" : null);
        if (name is null)
        {
            return;
        }

        var keywords = Keywords(node);
        var returnType = ReturnTypeText(node);
        var star = keywords.Contains("*") || node.Type == "generator_function_declaration" ? "*" : string.Empty;
        var signature = $"{(keywords.Contains("async") ? "async " : string.Empty)}function{star} {name}{TypeParametersText(node)}{ParametersText(node.GetChildForField("parameters"))}{(returnType is null ? string.Empty : ": " + returnType)}";
        var (start, end) = Lines(span);
        var modifiers = TopLevelVisibility(context, name) + (context.IsDefault ? " default" : string.Empty);
        var added = AddMemberEntry(new CodeMemberEntry(
            context.Scope?.Index,
            CodeMemberKind.Function,
            name,
            TypeScriptText.Squash(signature, MaxSignatureChars),
            returnType,
            modifiers,
            JoinLines(context.Decorators),
            context.Doc,
            start,
            end));
        var body = node.GetChildForField("body");
        if (added && body is not null)
        {
            AddCalls(body, context.Scope?.Name, name);
        }
    }

    private void AddVariables(Node node, Node span, Context context)
    {
        var keyword = node.Children.FirstOrDefault(c => c.Type is "const" or "let" or "var")?.Type ?? "const";
        foreach (var declarator in node.NamedChildren.Where(c => c.Type == "variable_declarator"))
        {
            var nameNode = declarator.GetChildForField("name");
            if (nameNode is null || nameNode.Type != "identifier")
            {
                // const { a, b } = require("x"): no symbol, but the right side may call things.
                AddCalls(declarator, context.Scope?.Name, null);
                continue;
            }

            var name = nameNode.Text;
            var value = declarator.GetChildForField("value");
            var typeNode = declarator.GetChildForField("type");
            var typeText = typeNode is null ? null : TrimTypeAnnotation(typeNode.Text);
            var (start, end) = Lines(span);
            var modifiers = TopLevelVisibility(context, name);
            if (value is not null && FunctionValues.Contains(value.Type))
            {
                var returnType = ReturnTypeText(value);
                var keywords = Keywords(value);
                var parameters = value.GetChildForField("parameters") ?? value.GetChildForField("parameter");
                var signature = $"{keyword} {name} = {(keywords.Contains("async") ? "async " : string.Empty)}{TypeParametersText(value)}{ParametersText(parameters)}{(returnType is null ? string.Empty : ": " + returnType)} =>";
                if (value.Type != "arrow_function")
                {
                    signature = $"{keyword} {name} = {(keywords.Contains("async") ? "async " : string.Empty)}function{TypeParametersText(value)}{ParametersText(parameters)}{(returnType is null ? string.Empty : ": " + returnType)}";
                }

                if (AddMemberEntry(new CodeMemberEntry(
                    context.Scope?.Index, CodeMemberKind.Function, name, TypeScriptText.Squash(signature, MaxSignatureChars), returnType, modifiers, JoinLines(context.Decorators), context.Doc, start, end)))
                {
                    AddCalls(value, context.Scope?.Name, name);
                }

                continue;
            }

            var text = $"{keyword} {name}{(typeText is null ? string.Empty : ": " + typeText)}{(value is null ? string.Empty : " = " + ValueText(value))}";
            if (AddMemberEntry(new CodeMemberEntry(
                context.Scope?.Index, CodeMemberKind.Variable, name, TypeScriptText.Squash(text, MaxSignatureChars), typeText, modifiers, JoinLines(context.Decorators), context.Doc, start, end))
                && value is not null)
            {
                AddCalls(value, context.Scope?.Name, name);
            }
        }
    }

    private bool AddMemberEntry(CodeMemberEntry entry)
    {
        if (_members.Count >= limits.MaxMembersPerFile)
        {
            _membersFull = true;
            return false;
        }

        _members.Add(entry);
        return true;
    }

    private bool CanAddType()
    {
        if (_types.Count < limits.MaxMembersPerFile)
        {
            return true;
        }

        _membersFull = true;
        return false;
    }

    /// <summary>Every call and construction under <paramref name="scopeNode"/>, found with an explicit stack so the depth of an expression does not matter.</summary>
    private void AddCalls(Node scopeNode, string? callerType, string? callerMember)
    {
        if (_callsFull)
        {
            return;
        }

        var pending = new Stack<Node>();
        pending.Push(scopeNode);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var node = pending.Pop();
            if (node.Type is "call_expression" or "new_expression")
            {
                var site = ReadCall(node, callerType, callerMember);
                if (site is not null)
                {
                    if (_calls.Count >= limits.MaxCallSitesPerFile)
                    {
                        _callsFull = true;
                        return;
                    }

                    _calls.Add(site);
                }
            }

            var children = node.NamedChildren;
            for (var i = children.Count - 1; i >= 0; i--)
            {
                pending.Push(children[i]);
            }
        }
    }

    private static CodeCallSiteEntry? ReadCall(Node call, string? callerType, string? callerMember)
    {
        var function = call.GetChildForField(call.Type == "new_expression" ? "constructor" : "function");
        if (function is null)
        {
            return null;
        }

        string callee;
        string? receiver = null;
        string? receiverType = null;
        var guessed = false;
        switch (function.Type)
        {
            case "identifier":
                callee = function.Text;
                break;
            case "member_expression":
                var property = function.GetChildForField("property");
                var target = function.GetChildForField("object");
                if (property is null || target is null)
                {
                    return null;
                }

                callee = property.Text;
                receiver = ReceiverOf(target);
                (receiverType, guessed) = ReceiverTypeOf(target, callerType);
                break;
            default:
                return null;
        }

        return string.IsNullOrEmpty(callee) || callee.Length > 200
            ? null
            : new CodeCallSiteEntry(callerType, callerMember, callee, receiver, call.StartPosition.Row + 1, receiverType, guessed);
    }

    /// <summary>The leftmost name of a receiver chain (<c>this</c> for <c>this.items.push()</c>); null when the chain does not start with a plain name.</summary>
    private static string? ReceiverOf(Node expression)
    {
        var current = expression;
        for (var depth = 0; depth < MaxChainDepth; depth++)
        {
            if (current.Type is "identifier" or "this")
            {
                return current.Text;
            }

            if (current.Type != "member_expression")
            {
                return null;
            }

            var inner = current.GetChildForField("object");
            if (inner is null)
            {
                return null;
            }

            current = inner;
        }

        return null;
    }

    /// <summary><c>this</c> is the enclosing class; a capitalised plain name is probably a class (a guess); everything else, including <c>this.service</c>, is unknown.</summary>
    private static (string? Type, bool Guessed) ReceiverTypeOf(Node target, string? callerType)
    {
        if (target.Type == "this")
        {
            return callerType is null ? (null, false) : (callerType, false);
        }

        if (target.Type != "identifier")
        {
            return (null, false);
        }

        var name = target.Text;
        return name.Length > 0 && char.IsUpper(name[0]) ? (name, true) : (null, false);
    }

    private string MemberModifiers(Node member, string name, HashSet<string> keywords, TypeScope scope)
    {
        var access = member.NamedChildren.FirstOrDefault(c => c.Type == "accessibility_modifier")?.Text;
        var visibility = name.StartsWith('#') ? "private" : access ?? "public";
        var parts = new List<string> { visibility };
        foreach (var keyword in new[] { "static", "abstract", "readonly", "async", "override" })
        {
            if (keywords.Contains(keyword))
            {
                parts.Add(keyword);
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>The anonymous keyword children of a node (static, async, get, set, abstract, readonly, *, const ...).</summary>
    private static HashSet<string> Keywords(Node node)
    {
        var keywords = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in node.Children)
        {
            if (!child.IsNamed && child.Type.Length > 0 && !char.IsPunctuation(child.Type[0]) || child.Type == "*")
            {
                keywords.Add(child.Type);
            }
        }

        return keywords;
    }

    /// <summary>The visibility of a top-level name: exported (or any name in a script or declaration file) is public, the rest is internal to the module.</summary>
    private string TopLevelVisibility(Context context, string name) =>
        context.Scope is not null ? "public" : context.Exported || _topLevelPublic || _namedExports.Contains(name) ? "public" : "internal";

    private static string? MemberName(Node? node)
    {
        if (node is null)
        {
            return null;
        }

        var text = TypeScriptText.Squash(node.Text, 200);
        if (text.Length == 0)
        {
            return null;
        }

        return node.Type == "string" ? TypeScriptText.Unquote(text) : text;
    }

    private static string? NameOf(Node node)
    {
        var name = node.GetChildForField("name");
        var text = name?.Text;
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string ParametersText(Node? parameters) =>
        parameters is null ? "()" : parameters.Type == "formal_parameters" ? TypeScriptText.Squash(parameters.Text, MaxSignatureChars) : "(" + TypeScriptText.Squash(parameters.Text, MaxSignatureChars) + ")";

    private static string TypeParametersText(Node node)
    {
        var typeParameters = node.GetChildForField("type_parameters");
        return typeParameters is null ? string.Empty : TypeScriptText.Squash(typeParameters.Text, MaxSignatureChars);
    }

    /// <summary>The declared return type without the leading colon; null when there is none.</summary>
    private static string? ReturnTypeText(Node node)
    {
        var annotation = node.GetChildForField("return_type");
        if (annotation is null)
        {
            return null;
        }

        var text = TrimTypeAnnotation(annotation.Text);
        return text.Length == 0 ? null : text;
    }

    private static string TrimTypeAnnotation(string text) =>
        TypeScriptText.Squash(text.TrimStart().TrimStart(':').Trim(), MaxSignatureChars);

    private static string ValueText(Node value) =>
        value.EndPosition.Row - value.StartPosition.Row > MaxValueRows ? Elided : TypeScriptText.Squash(value.Text, MaxValueChars);

    private static List<string> DecoratorsOf(Node node)
    {
        var decorators = new List<string>();
        foreach (var child in node.NamedChildren.Where(c => c.Type == "decorator"))
        {
            var text = DecoratorText(child);
            if (text.Length > 0)
            {
                decorators.Add(text);
            }
        }

        return decorators;
    }

    private static string DecoratorText(Node decorator) =>
        TypeScriptText.Squash(decorator.Text.TrimStart('@'), MaxAttributeChars);

    private static string? JoinLines(IReadOnlyList<string> lines) => lines.Count == 0 ? null : string.Join('\n', lines);

    private string Qualified(string name) => moduleName.Length == 0 ? name : $"{moduleName}.{name}";

    private string? ModuleOrNull() => moduleName.Length == 0 ? null : moduleName;

    /// <summary>1-based first and last line of a node. A node that ends at the start of a line ends on the line before.</summary>
    private (int Start, int End) Lines(Node node, int? startRowOverride = null)
    {
        var start = (startRowOverride ?? node.StartPosition.Row) + 1;
        var endRow = node.EndPosition.Row;
        if (node.EndPosition.Column == 0 && endRow > node.StartPosition.Row)
        {
            endRow--;
        }

        var end = Math.Max(start, endRow + 1);
        return (start, Math.Min(end, Math.Max(start, lineCount)));
    }
}
