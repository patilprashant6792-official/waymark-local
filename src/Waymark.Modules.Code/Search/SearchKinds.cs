using Waymark.Core.Code;

namespace Waymark.Modules.Code.Search;

/// <summary>The values accepted by code_search memberType. Class, Struct, Record... filter by declaration kind.</summary>
public enum SearchKind
{
    All,
    Class,
    Interface,
    Struct,
    Record,
    RecordStruct,
    Enum,
    Delegate,
    TypeAlias,
    Namespace,
    Method,
    Constructor,
    Property,
    Field,
    Event,
    Indexer,
    Operator,
    EnumMember,
    Function,
    Variable,
    Attribute
}

/// <summary>What a <see cref="SearchKind"/> asks the index for.</summary>
internal sealed record SearchScope(
    IReadOnlySet<CodeTypeKind> TypeKinds,
    IReadOnlySet<CodeMemberKind> MemberKinds,
    bool IncludeAttributes,
    bool IncludeBaseList)
{
    public static SearchScope For(SearchKind kind) => kind switch
    {
        SearchKind.All => new SearchScope(
            Enum.GetValues<CodeTypeKind>().ToHashSet(),
            Enum.GetValues<CodeMemberKind>().ToHashSet(),
            true,
            true),
        SearchKind.Attribute => new SearchScope(Empty<CodeTypeKind>(), Empty<CodeMemberKind>(), true, false),
        SearchKind.Interface => new SearchScope(new HashSet<CodeTypeKind> { CodeTypeKind.Interface }, Empty<CodeMemberKind>(), false, true),
        SearchKind.Class => OfType(CodeTypeKind.Class),
        SearchKind.Struct => OfType(CodeTypeKind.Struct),
        SearchKind.Record => OfType(CodeTypeKind.Record),
        SearchKind.RecordStruct => OfType(CodeTypeKind.RecordStruct),
        SearchKind.Enum => OfType(CodeTypeKind.Enum),
        SearchKind.Delegate => OfType(CodeTypeKind.Delegate),
        SearchKind.TypeAlias => OfType(CodeTypeKind.TypeAlias),
        SearchKind.Namespace => OfType(CodeTypeKind.Namespace),
        SearchKind.Method => OfMember(CodeMemberKind.Method),
        SearchKind.Constructor => OfMember(CodeMemberKind.Constructor),
        SearchKind.Property => OfMember(CodeMemberKind.Property),
        SearchKind.Field => OfMember(CodeMemberKind.Field),
        SearchKind.Event => OfMember(CodeMemberKind.Event),
        SearchKind.Indexer => OfMember(CodeMemberKind.Indexer),
        SearchKind.Operator => OfMember(CodeMemberKind.Operator),
        SearchKind.EnumMember => OfMember(CodeMemberKind.EnumMember),
        SearchKind.Function => OfMember(CodeMemberKind.Function),
        SearchKind.Variable => OfMember(CodeMemberKind.Variable),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static bool TryParse(string? text, out SearchKind kind)
    {
        kind = SearchKind.All;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var trimmed = text.Trim();
        return !char.IsDigit(trimmed[0]) && Enum.TryParse(trimmed, ignoreCase: true, out kind) && Enum.IsDefined(kind);
    }

    private static SearchScope OfType(CodeTypeKind kind) =>
        new(new HashSet<CodeTypeKind> { kind }, Empty<CodeMemberKind>(), false, false);

    private static SearchScope OfMember(CodeMemberKind kind) =>
        new(Empty<CodeTypeKind>(), new HashSet<CodeMemberKind> { kind }, false, false);

    private static HashSet<T> Empty<T>() => [];
}
