namespace Waymark.Modules.Code.Search;

public interface ICodeSearchService
{
    /// <param name="terms">Extra words that widen the search (synonyms, other spellings); counted as half a word each in the ranking.</param>
    /// <param name="matchIn">All (default), Names, Docs or Code.</param>
    /// <param name="pathGlob">Only hits in files matching this glob.</param>
    /// <param name="includeTests">False leaves out hits in test folders and reports their number.</param>
    /// <exception cref="ArgumentException">Empty or too long query, unknown memberType or matchIn, too many words, or '*' with no enabled workspace.</exception>
    Task<string> SearchAsync(
        string? workspace,
        string? query,
        string? memberType,
        bool caseSensitive,
        int? pageSize,
        int? page,
        IReadOnlyList<string>? terms,
        string? matchIn,
        string? pathGlob,
        bool includeTests,
        CancellationToken ct);
}

public interface ICodeCallersService
{
    /// <exception cref="ArgumentException">The file is not an indexed C# file, or the member is missing or ambiguous.</exception>
    Task<string> CallersAsync(
        string? workspace,
        string? relativeFilePath,
        string? methodName,
        string? className,
        bool includeTests,
        bool includeUnrelated,
        int? pageSize,
        int? page,
        CancellationToken ct);
}

public interface ICodeTypesService
{
    /// <exception cref="ArgumentException">The type name is empty or unknown, or the direction is not one of both, derived, bases.</exception>
    Task<string> TypesAsync(
        string? workspace,
        string? typeName,
        string? direction,
        string? memberName,
        bool includeTests,
        CancellationToken ct);
}
