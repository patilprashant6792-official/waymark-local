using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Waymark.Core.Security;
using Waymark.Modules.Code.Search;

namespace Waymark.Modules.Code.Tools;

[McpServerToolType]
public sealed class CodeSearchTools(ICodeSearchService search, ICodeCallersService callers, ICodeTypesService types)
{
    [McpServerTool(Name = "code_search", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Global code search across workspace(s) - finds types (classes, interfaces, records, structs, enums, delegates), members (methods, constructors, properties, fields, events) and attributes by name or keyword. " +
        "WILDCARD SUPPORT: pass workspace='*' to search ALL enabled workspaces at once. " +
        "Matching is forgiving and ranked best first: exact, prefix, camel-case initials ('PSS' finds ProjectSkeletonService), word start ('Skeleton'), several words ('search global' finds SearchGloballyAsync), substring, then characters in order. " +
        "Interface also finds the base-list entries of types ('IUserService' finds its implementers); Attribute finds attribute use ('HttpPost'). " +
        "Examples: code_search('MyApi', 'Redis') | code_search('*', 'Authorize', memberType='Attribute') | code_search('*', 'IUserService', memberType='Interface'). " +
        "Use cases: security audits, dependency analysis, finding usages before a rename. " +
        "Results come from the syntax index (no symbol resolution).")]
    public Task<string> Search(
        [Description("Search text: a type or member name, a keyword, camel-case initials, or several words (e.g. 'Redis', 'UserService', 'Authorize')")]
        string query,
        [Description("Workspace name as shown in Waymark, or '*' for ALL enabled workspaces")]
        string workspace,
        [Description("Optional: All (default), Class, Interface, Struct, Record, RecordStruct, Enum, Delegate, Method, Constructor, Property, Field, Event, Indexer, Operator, EnumMember, Attribute")]
        string memberType = "All",
        [Description("Optional: case-sensitive search (default false, for broader matches)")]
        bool caseSensitive = false,
        [Description("Optional: results per page (default 20, max 200). Use with 'page' to paginate large result sets.")]
        int? pageSize = null,
        [Description("Optional: page number, 1-based (default 1). Increment to retrieve subsequent pages.")]
        int? page = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => search.SearchAsync(workspace, query, memberType, caseSensitive, pageSize, page, cancellationToken));

    [McpServerTool(Name = "code_callers", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Shows WHO calls a method and WHERE exactly, and what the method itself calls. " +
        "CRITICAL for impact analysis. Returns caller locations (file, line, calling type and member, receiver) and the calls made inside the method. " +
        "Use this BEFORE: changing a method signature (prevents breaking callers), modifying behavior, deleting a method, or renaming a method (find all references). " +
        "Example: before changing GetUser(), verify which services depend on its parameter order. " +
        "Callers are found by method NAME in the syntax index, then sorted by the declared type of their receiver: calls on an unrelated type are left out and counted (includeUnrelated=true lists them), and '?' marks a call whose receiver type is unknown. All overloads share one result.")]
    public Task<string> Callers(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("Path of the C# file that defines the method, relative to the workspace root (e.g. 'Services/UserService.cs')")]
        string relativeFilePath,
        [Description("Method name to analyze (e.g. 'GetUser', 'ProcessOrder')")]
        string methodName,
        [Description("Optional: class name when the file has several classes with this method")]
        string? className = null,
        [Description("Optional: include callers in test folders (default false, production code only; their number is reported)")]
        bool includeTests = false,
        [Description("Optional: also list calls whose receiver is declared as an unrelated type, marked [on Type] (default false: they are only counted)")]
        bool includeUnrelated = false,
        [Description("Optional: callers per page (default 20, max 200). Use with 'page' when a method has many callers.")]
        int? pageSize = null,
        [Description("Optional: page number, 1-based (default 1). Increment to see more callers.")]
        int? page = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => callers.CallersAsync(workspace, relativeFilePath, methodName, className, includeTests, includeUnrelated, pageSize, page, cancellationToken));

    [McpServerTool(Name = "code_types", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Shows what IMPLEMENTS or DERIVES from a type, and what it builds on: the whole chain, not just the direct level, with file and line. " +
        "Use this BEFORE changing an interface or base class (every implementer and subclass is affected), or to answer 'who implements IFoo' / 'what does this class inherit'. " +
        "Pass memberName to see which of those types declare that member (the implementations or overrides of a method). " +
        "Read from the base lists in the syntax index and matched by simple name; a base type from a library is shown as not in the index.")]
    public Task<string> Types(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("Type name, e.g. 'IAuditStore' or 'OrderService' (a namespace or type arguments are ignored)")]
        string typeName,
        [Description("Optional: 'both' (default), 'derived' (implementers and subclasses only) or 'bases' (what it builds on only)")]
        string? direction = null,
        [Description("Optional: a member name; marks the types that declare it, e.g. 'UsageAsync' to find its implementations")]
        string? memberName = null,
        [Description("Optional: include derived types in test folders (default true)")]
        bool includeTests = true,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => types.TypesAsync(workspace, typeName, direction, memberName, includeTests, cancellationToken));

    private static async Task<string> RunAsync(Func<Task<string>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileAccessDeniedException or ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            throw new McpException(ex.Message);
        }
    }
}
