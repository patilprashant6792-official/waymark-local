namespace Waymark.Core.Code;

/// <summary>Read-only lookups over the code index for code_search, code_callers and code_types.</summary>
public interface ICodeSearchStore
{
    /// <exception cref="ArgumentException">The query has no tokens.</exception>
    Task<CodeSymbolResult> FindSymbolsAsync(CodeSymbolQuery query, CancellationToken ct);

    /// <summary>Call sites whose callee name equals <paramref name="calleeName"/> exactly (case-sensitive), ordered by path and line.</summary>
    Task<CodeCallSiteResult> FindCallSitesAsync(long workspaceId, string calleeName, int limit, CancellationToken ct);

    /// <summary>Every type of the workspace that has a base list (the edges of the type hierarchy), ordered by path and line.</summary>
    Task<CodeTypeRelations> FindTypeRelationsAsync(long workspaceId, int limit, CancellationToken ct);

    /// <summary>Types whose simple name matches one of <paramref name="names"/> (ignoring case), with or without a base list.</summary>
    Task<IReadOnlyList<CodeTypeRow>> FindTypesByNameAsync(long workspaceId, IReadOnlyCollection<string> names, CancellationToken ct);

    /// <summary>The members called <paramref name="name"/> (ignoring case) with the type that declares each, ordered by path and line.</summary>
    Task<IReadOnlyList<CodeMemberLocation>> FindMembersByNameAsync(long workspaceId, string name, int limit, CancellationToken ct);

    /// <summary>Which of <paramref name="names"/> are declared as a field, property or event somewhere in the workspace (exact spelling).</summary>
    Task<IReadOnlySet<string>> FindDeclaredValueNamesAsync(long workspaceId, IReadOnlyCollection<string> names, CancellationToken ct);
}
