namespace Waymark.Core.Code;

/// <summary>
/// A candidate lookup over the code index. <see cref="Tokens"/> are the words of the user's query: one token is matched
/// with its characters in order (so 'PSS' can find 'ProjectSkeletonService'), several tokens must all occur in the name.
/// The store only pre-filters; the caller ranks the candidates.
/// </summary>
/// <param name="TypeKinds">Type kinds whose name is searched; empty searches no type name.</param>
/// <param name="MemberKinds">Member kinds whose name is searched; empty searches no member name.</param>
/// <param name="IncludeAttributes">Also return types and members whose attribute text contains every token.</param>
/// <param name="IncludeBaseList">Also return types whose base list contains every token.</param>
/// <param name="Limit">Most candidates returned per table (types, members).</param>
public sealed record CodeSymbolQuery(
    IReadOnlyList<long> WorkspaceIds,
    IReadOnlyList<string> Tokens,
    bool CaseSensitive,
    IReadOnlySet<CodeTypeKind> TypeKinds,
    IReadOnlySet<CodeMemberKind> MemberKinds,
    bool IncludeAttributes,
    bool IncludeBaseList,
    int Limit);

/// <param name="Kind">Lower-case name of a <see cref="CodeTypeKind"/> or <see cref="CodeMemberKind"/>, depending on <paramref name="IsType"/>.</param>
/// <param name="Owner">Name of the declaring type for a member; null for a type or a member outside any type.</param>
public sealed record CodeSymbolRow(
    long WorkspaceId,
    string RelativePath,
    bool IsType,
    string Kind,
    string Name,
    string? Owner,
    string? Signature,
    string? Modifiers,
    string? Attributes,
    string? BaseList,
    int Line);

/// <param name="Capped">True when a table had more candidates than <see cref="CodeSymbolQuery.Limit"/>.</param>
public sealed record CodeSymbolResult(IReadOnlyList<CodeSymbolRow> Rows, bool Capped);

/// <summary>
/// An invocation by name. The receiver is the leftmost name of the receiver chain, null for an unqualified call;
/// <paramref name="ReceiverType"/> is the declared type of the actual receiver when the file told it.
/// </summary>
public sealed record CodeCallSiteRow(
    string RelativePath,
    string? CallerType,
    string? CallerMember,
    string? Receiver,
    int Line,
    string? ReceiverType = null,
    bool ReceiverTypeGuessed = false);

/// <param name="Capped">True when more call sites exist than the limit that was asked for.</param>
public sealed record CodeCallSiteResult(IReadOnlyList<CodeCallSiteRow> Rows, bool Capped);

/// <summary>A type declaration with its base list, as needed to walk the type hierarchy.</summary>
/// <param name="Kind">Lower-case name of a <see cref="CodeTypeKind"/>.</param>
public sealed record CodeTypeRow(string Name, string FullName, string Kind, string? Namespace, string? BaseList, string RelativePath, int Line);

/// <param name="Capped">True when more types with a base list exist than the limit that was asked for.</param>
public sealed record CodeTypeRelations(IReadOnlyList<CodeTypeRow> Rows, bool Capped);

/// <summary>Where a member name is declared. <paramref name="OwnerFullName"/> is null for a member outside any type.</summary>
public sealed record CodeMemberLocation(string? OwnerFullName, string Kind, string Signature, string RelativePath, int Line);
