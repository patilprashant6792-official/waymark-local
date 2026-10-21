namespace Waymark.Modules.Code.Search;

public interface ICodeSearchService
{
    /// <exception cref="ArgumentException">Empty or too long query, unknown memberType, or '*' with no enabled workspace.</exception>
    Task<string> SearchAsync(string? workspace, string? query, string? memberType, bool caseSensitive, int? pageSize, int? page, CancellationToken ct);
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
