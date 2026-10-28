using TreeSitter;
using Waymark.Core.Code;

namespace Waymark.Languages.Python;

internal sealed record PythonExtraction(
    IReadOnlyList<string> Imports,
    IReadOnlyList<CodeTypeEntry> Types,
    IReadOnlyList<CodeMemberEntry> Members,
    IReadOnlyList<CodeCallSiteEntry> CallSites,
    bool Truncated);

/// <summary>
/// Reads the declarations of one parsed Python module into types, members and call sites. Statements are walked recursively (nesting is shallow);
/// expressions are scanned with an explicit stack, so a very deep expression cannot overflow the stack. One instance per file.
/// </summary>
/// <remarks>
/// Mapping: a class is a Class (Interface for a Protocol, Enum for an Enum subclass, Record for a dataclass or a pydantic model). A def in a class is a Method,
/// <c>__init__</c> a Constructor named after its class, a <c>@property</c> a Property. A def outside any class is a Function, an assignment outside any class a Variable.
/// Decorators are the attributes. The first line of a docstring is the summary. Names that start with an underscore are private; when the module has
/// <c>__all__</c>, a top-level name it does not list is internal.
/// </remarks>
internal sealed class PythonExtractor(string moduleName, int lineCount, CodeIndexLimits limits, CancellationToken ct)
{
    private const int MaxSignatureChars = 1000;
    private const int MaxAttributeChars = 500;
    private const int MaxImportChars = 300;
    private const int MaxSummaryChars = 500;
    private const int MaxValueChars = 80;
    private const int MaxValueRows = 5;
    private const int MaxImports = 1000;
    private const int MaxNesting = 48;
    private const int MaxChainDepth = 64;
    private const string Elided = "...";

    // Statements that hold other statements. Definitions inside them (if TYPE_CHECKING, try/except ImportError ...) belong to the enclosing scope.
    private static readonly HashSet<string> CompoundStatements = new(StringComparer.Ordinal)
    {
        "if_statement", "elif_clause", "else_clause", "try_statement", "except_clause", "except_group_clause", "finally_clause",
        "with_statement", "for_statement", "while_statement", "match_statement", "case_clause", "block",
    };

    private static readonly HashSet<string> EnumBases = new(StringComparer.Ordinal) { "Enum", "IntEnum", "StrEnum", "Flag", "IntFlag", "ReprEnum" };

    private static readonly HashSet<string> RecordBases = new(StringComparer.Ordinal) { "BaseModel", "NamedTuple", "TypedDict", "BaseSettings", "SQLModel" };

    private static readonly HashSet<string> RecordDecorators = new(StringComparer.Ordinal)
    {
        "dataclass", "dataclasses.dataclass", "attr.s", "attr.attrs", "attr.define", "attr.frozen", "attrs.define", "attrs.frozen", "define", "frozen",
    };

    private readonly List<string> _imports = [];
    private readonly HashSet<string> _seenImports = new(StringComparer.Ordinal);
    private readonly List<CodeTypeEntry> _types = [];
    private readonly List<CodeMemberEntry> _members = [];
    private readonly List<CodeCallSiteEntry> _calls = [];
    private readonly Dictionary<int, HashSet<string>> _attributeNames = new();
    private HashSet<string>? _exported;
    private bool _membersFull;
    private bool _callsFull;

    private sealed record ClassScope(int Index, string Name, string FullName, bool IsEnum);

    public PythonExtraction Extract(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _exported = ReadExports(root);
        WalkStatements(root.NamedChildren, null, 0);
        return new PythonExtraction(_imports, _types, _members, _calls, _membersFull || _callsFull);
    }

    private void WalkStatements(IReadOnlyList<Node> statements, ClassScope? scope, int depth)
    {
        foreach (var statement in statements)
        {
            ct.ThrowIfCancellationRequested();
            if (_membersFull)
            {
                return;
            }

            WalkStatement(statement, scope, depth);
        }
    }

    private void WalkStatement(Node node, ClassScope? scope, int depth)
    {
        switch (node.Type)
        {
            case "import_statement":
            case "import_from_statement":
            case "future_import_statement":
                AddImport(node);
                break;
            case "class_definition":
                AddClass(node, node, scope, [], depth);
                break;
            case "function_definition":
                AddFunction(node, node, scope, []);
                break;
            case "decorated_definition":
                AddDecorated(node, scope, depth);
                break;
            case "expression_statement":
                AddExpressionStatement(node, scope);
                break;
            case "type_alias_statement":
                AddTypeAliasStatement(node, scope);
                break;
            default:
                if (depth < MaxNesting && CompoundStatements.Contains(node.Type))
                {
                    WalkStatements(node.NamedChildren, scope, depth + 1);
                }

                break;
        }
    }

    private void AddImport(Node node)
    {
        if (_imports.Count >= MaxImports)
        {
            return;
        }

        var text = PythonText.Squash(node.Text, MaxImportChars);
        if (text.Length > 0 && _seenImports.Add(text))
        {
            _imports.Add(text);
        }
    }

    private void AddDecorated(Node node, ClassScope? scope, int depth)
    {
        var definition = node.GetChildForField("definition");
        if (definition is null)
        {
            return;
        }

        var decorators = DecoratorsOf(node);
        switch (definition.Type)
        {
            case "class_definition":
                AddClass(definition, node, scope, decorators, depth);
                break;
            case "function_definition":
                AddFunction(definition, node, scope, decorators);
                break;
        }
    }

    private void AddClass(Node definition, Node span, ClassScope? outer, IReadOnlyList<string> decorators, int depth)
    {
        var name = NameOf(definition);
        if (name is null || depth >= MaxNesting)
        {
            return;
        }

        if (_types.Count >= limits.MaxMembersPerFile)
        {
            _membersFull = true;
            return;
        }

        var fullName = outer is not null ? $"{outer.FullName}.{name}" : Qualified(name);
        var bases = ReadBases(definition);
        var simpleBases = bases.Select(SimpleName).ToList();
        var kind = KindOfClass(simpleBases, decorators);
        var index = _types.Count;
        var (start, end) = Lines(span);
        var body = definition.GetChildForField("body");
        var modifiers = Visibility(name, outer);
        if (simpleBases.Contains("ABC", StringComparer.Ordinal))
        {
            modifiers += " abstract";
        }

        _types.Add(new CodeTypeEntry(
            kind,
            name,
            fullName,
            ModuleOrNull(),
            bases.Count == 0 ? null : PythonText.Squash(string.Join(", ", bases), MaxSignatureChars),
            modifiers,
            JoinDecorators(decorators),
            body is null ? null : ReadDocstring(body),
            start,
            end));

        if (body is null)
        {
            return;
        }

        var scope = new ClassScope(index, name, fullName, kind == CodeTypeKind.Enum);
        _attributeNames[index] = new HashSet<string>(StringComparer.Ordinal);
        WalkStatements(body.NamedChildren, scope, depth + 1);
    }

    private void AddFunction(Node definition, Node span, ClassScope? scope, IReadOnlyList<string> decorators)
    {
        var name = NameOf(definition);
        if (name is null)
        {
            return;
        }

        var parameters = definition.GetChildForField("parameters");
        var returnType = definition.GetChildForField("return_type");
        var typeParameters = definition.GetChildForField("type_parameters");
        var parameterText = parameters is null ? "()" : PythonText.Squash(parameters.Text, MaxSignatureChars);
        var returnText = returnType is null ? null : PythonText.Squash(returnType.Text, MaxSignatureChars);
        var generic = typeParameters is null ? string.Empty : PythonText.Squash(typeParameters.Text, MaxSignatureChars);
        var signature =
            $"{(IsAsync(definition) ? "async " : string.Empty)}def {name}{generic}{parameterText}{(returnText is null ? string.Empty : " -> " + returnText)}";

        var kind = KindOfFunction(scope, decorators, name, out var skipEntry);
        var memberName = kind == CodeMemberKind.Constructor && scope is not null ? scope.Name : name;
        var body = definition.GetChildForField("body");
        if (!skipEntry)
        {
            var (start, end) = Lines(span);
            var added = AddMemberEntry(new CodeMemberEntry(
                scope?.Index,
                kind,
                memberName,
                PythonText.Squash(signature, MaxSignatureChars),
                returnText,
                FunctionModifiers(name, scope, decorators),
                JoinDecorators(decorators),
                body is null ? null : ReadDocstring(body),
                start,
                end));
            if (!added)
            {
                return;
            }
        }

        AddCalls(span, scope?.Name, memberName);
        if (kind == CodeMemberKind.Constructor && scope is not null && body is not null)
        {
            AddInstanceAttributes(body, scope);
        }
    }

    private void AddExpressionStatement(Node statement, ClassScope? scope)
    {
        foreach (var child in statement.NamedChildren)
        {
            if (child.Type == "assignment")
            {
                AddAssignment(child, statement, scope);
            }
            else
            {
                AddCalls(child, scope?.Name, null);
            }
        }
    }

    private void AddAssignment(Node assignment, Node statement, ClassScope? scope)
    {
        var left = assignment.GetChildForField("left");
        if (left is null || left.Type != "identifier")
        {
            // A tuple, an attribute or a subscript on the left: no symbol, but the right side may call things.
            AddCalls(statement, scope?.Name, null);
            return;
        }

        var name = left.Text;
        if (name.Length == 0 || name == "__all__")
        {
            AddCalls(statement, scope?.Name, null);
            return;
        }

        var typeNode = assignment.GetChildForField("type");
        var right = assignment.GetChildForField("right");
        var annotation = typeNode is null ? null : PythonText.Squash(typeNode.Text, MaxSignatureChars);
        if (annotation is not null && IsTypeAliasMarker(annotation))
        {
            AddTypeAlias(name, statement, scope);
            AddCalls(statement, scope?.Name, name);
            return;
        }

        var value = right is null
            ? null
            : right.EndPosition.Row - right.StartPosition.Row > MaxValueRows ? Elided : PythonText.Squash(right.Text, MaxValueChars);
        var (start, end) = Lines(statement);

        CodeMemberEntry entry;
        if (scope is { IsEnum: true } && annotation is null && !name.StartsWith('_'))
        {
            entry = new CodeMemberEntry(
                scope.Index, CodeMemberKind.EnumMember, name, value is null ? name : $"{name} = {value}", null, "public", null, null, start, end);
        }
        else
        {
            if (scope is not null && !_attributeNames[scope.Index].Add(name))
            {
                AddCalls(statement, scope.Name, name);
                return;
            }

            var signature = annotation is null
                ? (value is null ? name : $"{name} = {value}")
                : (value is null ? $"{name}: {annotation}" : $"{name}: {annotation} = {value}");
            entry = new CodeMemberEntry(
                scope?.Index,
                scope is null ? CodeMemberKind.Variable : CodeMemberKind.Field,
                name,
                PythonText.Squash(signature, MaxSignatureChars),
                annotation,
                Visibility(name, scope),
                null,
                null,
                start,
                end);
        }

        if (AddMemberEntry(entry))
        {
            AddCalls(statement, scope?.Name, name);
        }
    }

    private void AddTypeAliasStatement(Node node, ClassScope? scope)
    {
        var left = node.GetChildForField("left") ?? (node.NamedChildren.Count > 0 ? node.NamedChildren[0] : null);
        if (left is null)
        {
            return;
        }

        var text = left.Text;
        var bracket = text.IndexOf('[', StringComparison.Ordinal);
        var name = (bracket < 0 ? text : text[..bracket]).Trim();
        if (name.Length > 0)
        {
            AddTypeAlias(name, node, scope);
        }
    }

    private void AddTypeAlias(string name, Node span, ClassScope? scope)
    {
        if (_types.Count >= limits.MaxMembersPerFile)
        {
            _membersFull = true;
            return;
        }

        var (start, end) = Lines(span);
        var fullName = scope is not null ? $"{scope.FullName}.{name}" : Qualified(name);
        _types.Add(new CodeTypeEntry(CodeTypeKind.TypeAlias, name, fullName, ModuleOrNull(), null, Visibility(name, scope), null, null, start, end));
    }

    /// <summary>The attributes a constructor gives its instance (<c>self.name = value</c>), as Field members of the class.</summary>
    private void AddInstanceAttributes(Node body, ClassScope scope)
    {
        var seen = _attributeNames[scope.Index];
        var pending = new Stack<Node>();
        PushReversed(pending, body.NamedChildren);
        while (pending.Count > 0 && !_membersFull)
        {
            ct.ThrowIfCancellationRequested();
            var node = pending.Pop();
            if (node.Type == "expression_statement")
            {
                foreach (var child in node.NamedChildren)
                {
                    if (child.Type == "assignment")
                    {
                        AddInstanceAttribute(child, node, scope, seen);
                    }
                }
            }
            else if (CompoundStatements.Contains(node.Type))
            {
                PushReversed(pending, node.NamedChildren);
            }
        }
    }

    private void AddInstanceAttribute(Node assignment, Node statement, ClassScope scope, HashSet<string> seen)
    {
        var left = assignment.GetChildForField("left");
        if (left is null || left.Type != "attribute")
        {
            return;
        }

        var owner = left.GetChildForField("object");
        var attribute = left.GetChildForField("attribute");
        if (owner is null || attribute is null || owner.Type != "identifier" || owner.Text != "self")
        {
            return;
        }

        var name = attribute.Text;
        if (name.Length == 0 || !seen.Add(name))
        {
            return;
        }

        var typeNode = assignment.GetChildForField("type");
        var annotation = typeNode is null ? null : PythonText.Squash(typeNode.Text, MaxSignatureChars);
        var (start, end) = Lines(statement);
        AddMemberEntry(new CodeMemberEntry(
            scope.Index,
            CodeMemberKind.Field,
            name,
            annotation is null ? $"self.{name}" : $"self.{name}: {annotation}",
            annotation,
            Visibility(name, scope),
            null,
            null,
            start,
            end));
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

    /// <summary>Every call under <paramref name="scopeNode"/>, found with an explicit stack so the depth of an expression does not matter.</summary>
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
            if (node.Type == "call")
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

            PushReversed(pending, node.NamedChildren);
        }
    }

    private static void PushReversed(Stack<Node> stack, IReadOnlyList<Node> nodes)
    {
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            stack.Push(nodes[i]);
        }
    }

    private static CodeCallSiteEntry? ReadCall(Node call, string? callerType, string? callerMember)
    {
        var function = call.GetChildForField("function");
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
            case "attribute":
                var attribute = function.GetChildForField("attribute");
                var target = function.GetChildForField("object");
                if (attribute is null || target is null)
                {
                    return null;
                }

                callee = attribute.Text;
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

    /// <summary>The leftmost name of a receiver chain (<c>self</c> for <c>self.items.append()</c>); null when the chain does not start with a plain name.</summary>
    private static string? ReceiverOf(Node expression)
    {
        var current = expression;
        for (var depth = 0; depth < MaxChainDepth; depth++)
        {
            if (current.Type == "identifier")
            {
                return current.Text;
            }

            if (current.Type != "attribute")
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

    /// <summary>
    /// What the file alone tells about the receiver: <c>self</c> and <c>cls</c> are the enclosing class; a capitalised plain name is probably a class
    /// (a guess); everything else is unknown. A chain such as <c>self.service</c> is deliberately unknown: it is an attribute, not the class.
    /// </summary>
    private static (string? Type, bool Guessed) ReceiverTypeOf(Node target, string? callerType)
    {
        if (target.Type != "identifier")
        {
            return (null, false);
        }

        var name = target.Text;
        if (callerType is not null && (name == "self" || name == "cls"))
        {
            return (callerType, false);
        }

        return name.Length > 0 && char.IsUpper(name[0]) ? (name, true) : (null, false);
    }

    private static HashSet<string>? ReadExports(Node root)
    {
        HashSet<string>? exported = null;
        foreach (var statement in root.NamedChildren)
        {
            if (statement.Type != "expression_statement")
            {
                continue;
            }

            foreach (var child in statement.NamedChildren)
            {
                if (child.Type is not ("assignment" or "augmented_assignment"))
                {
                    continue;
                }

                var left = child.GetChildForField("left");
                var right = child.GetChildForField("right");
                if (left is null || right is null || left.Type != "identifier" || left.Text != "__all__" || right.Type is not ("list" or "tuple"))
                {
                    continue;
                }

                exported ??= new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in right.NamedChildren)
                {
                    var value = item.Type == "string" ? PythonText.Unquote(item.Text) : null;
                    if (!string.IsNullOrEmpty(value))
                    {
                        exported.Add(value);
                    }
                }
            }
        }

        return exported;
    }

    private static string? ReadDocstring(Node body)
    {
        foreach (var statement in body.NamedChildren)
        {
            if (statement.Type == "comment")
            {
                continue;
            }

            if (statement.Type != "expression_statement")
            {
                return null;
            }

            var children = statement.NamedChildren;
            return children.Count == 1 && children[0].Type == "string" ? PythonText.Summary(children[0].Text, MaxSummaryChars) : null;
        }

        return null;
    }

    private static List<string> ReadBases(Node definition)
    {
        var bases = new List<string>();
        var superclasses = definition.GetChildForField("superclasses");
        if (superclasses is null)
        {
            return bases;
        }

        foreach (var child in superclasses.NamedChildren)
        {
            // metaclass=ABCMeta and **options are arguments of the class statement, not bases.
            if (child.Type is "keyword_argument" or "comment" or "dictionary_splat" or "list_splat")
            {
                continue;
            }

            var text = PythonText.Squash(child.Text, 200);
            if (text.Length > 0)
            {
                bases.Add(text);
            }
        }

        return bases;
    }

    private static List<string> DecoratorsOf(Node decorated)
    {
        var decorators = new List<string>();
        foreach (var child in decorated.NamedChildren)
        {
            if (child.Type != "decorator")
            {
                continue;
            }

            var text = PythonText.Squash(child.Text.TrimStart('@'), MaxAttributeChars);
            if (text.Length > 0)
            {
                decorators.Add(text);
            }
        }

        return decorators;
    }

    private static CodeTypeKind KindOfClass(IReadOnlyList<string> simpleBases, IReadOnlyList<string> decorators)
    {
        if (simpleBases.Contains("Protocol", StringComparer.Ordinal))
        {
            return CodeTypeKind.Interface;
        }

        if (simpleBases.Any(EnumBases.Contains))
        {
            return CodeTypeKind.Enum;
        }

        if (decorators.Any(d => RecordDecorators.Contains(DecoratorName(d))) || simpleBases.Any(RecordBases.Contains))
        {
            return CodeTypeKind.Record;
        }

        return CodeTypeKind.Class;
    }

    private static CodeMemberKind KindOfFunction(ClassScope? scope, IReadOnlyList<string> decorators, string name, out bool skipEntry)
    {
        skipEntry = false;
        if (scope is null)
        {
            return CodeMemberKind.Function;
        }

        foreach (var decorator in decorators)
        {
            var decoratorName = DecoratorName(decorator);
            var last = LastSegment(decoratorName);
            if ((last == "setter" || last == "deleter") && decoratorName.Contains('.', StringComparison.Ordinal))
            {
                // The getter already stands for the property.
                skipEntry = true;
                return CodeMemberKind.Property;
            }

            if (last == "property" || last == "cached_property")
            {
                return CodeMemberKind.Property;
            }
        }

        return name == "__init__" ? CodeMemberKind.Constructor : CodeMemberKind.Method;
    }

    private string FunctionModifiers(string name, ClassScope? scope, IReadOnlyList<string> decorators)
    {
        var modifiers = Visibility(name, scope);
        if (HasDecorator(decorators, "staticmethod") || HasDecorator(decorators, "classmethod"))
        {
            modifiers += " static";
        }

        if (HasDecorator(decorators, "abstractmethod"))
        {
            modifiers += " abstract";
        }

        return modifiers;
    }

    /// <summary>public, private (a leading underscore) or internal (a top-level name that <c>__all__</c> leaves out). Dunder names such as <c>__init__</c> are public.</summary>
    private string Visibility(string name, ClassScope? scope)
    {
        var dunder = name.Length > 4 && name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal);
        if (!dunder && name.StartsWith('_'))
        {
            return "private";
        }

        return scope is null && _exported is not null && !_exported.Contains(name) ? "internal" : "public";
    }

    private static bool HasDecorator(IReadOnlyList<string> decorators, string lastSegment) =>
        decorators.Any(d => LastSegment(DecoratorName(d)) == lastSegment);

    /// <summary>The decorator without its arguments: <c>app.get</c> for <c>app.get("/users")</c>.</summary>
    private static string DecoratorName(string decorator)
    {
        var open = decorator.IndexOf('(', StringComparison.Ordinal);
        return (open < 0 ? decorator : decorator[..open]).Trim();
    }

    private static string LastSegment(string dotted) => dotted[(dotted.LastIndexOf('.') + 1)..];

    private static string SimpleName(string baseText)
    {
        var bracket = baseText.IndexOf('[', StringComparison.Ordinal);
        var bare = bracket < 0 ? baseText : baseText[..bracket];
        return bare[(bare.LastIndexOf('.') + 1)..].Trim();
    }

    private static bool IsTypeAliasMarker(string annotation) =>
        annotation == "TypeAlias" || annotation.EndsWith(".TypeAlias", StringComparison.Ordinal);

    private static bool IsAsync(Node definition)
    {
        var children = definition.Children;
        for (var i = 0; i < children.Count && i < 2; i++)
        {
            if (children[i].Type == "async")
            {
                return true;
            }
        }

        return false;
    }

    private static string? NameOf(Node definition)
    {
        var name = definition.GetChildForField("name");
        var text = name?.Text;
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string? JoinDecorators(IReadOnlyList<string> decorators) =>
        decorators.Count == 0 ? null : string.Join('\n', decorators);

    private string Qualified(string name) => moduleName.Length == 0 ? name : $"{moduleName}.{name}";

    private string? ModuleOrNull() => moduleName.Length == 0 ? null : moduleName;

    /// <summary>1-based first and last line of a node. A node that ends at the start of a line ends on the line before.</summary>
    private (int Start, int End) Lines(Node node)
    {
        var start = node.StartPosition.Row + 1;
        var endRow = node.EndPosition.Row;
        if (node.EndPosition.Column == 0 && endRow > node.StartPosition.Row)
        {
            endRow--;
        }

        var end = Math.Max(start, endRow + 1);
        return (start, Math.Min(end, Math.Max(start, lineCount)));
    }
}
