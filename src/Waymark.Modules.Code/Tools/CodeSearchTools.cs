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
        "Global code search across workspace(s): finds types (classes, interfaces, records, structs, enums, delegates, type aliases), members (methods, constructors, properties, fields, events, functions, variables) and attributes (decorators) in C#, Python, TypeScript and JavaScript files. " +
        "Two ways to ask. (1) By name: 'UserService', 'PSS' (camel-case initials find ProjectSkeletonService), 'Skeleton' (word start), 'search global' (several words in one name). An exact name always comes first. " +
        "(2) By description: 'refresh token rotation', 'retry failed upload'. Besides names, the search reads the split words of names, doc comments, file paths and the words used inside member bodies (identifiers, comments, string words), so code is found even when its name says none of the query. Stemming applies (creating finds Create). " +
        "Results are ranked best first (words covered, kind of symbol, how often a method is called, tests last); each hit of a multi-word query lists the words it matched. " +
        "Query syntax: plain words all help and more matches rank higher; \"two words\" in quotes must be adjacent; +word must match; -word must not match. " +
        "Pass terms with synonyms or other spellings you expect in the code (terms=['jwt','bearer']) to widen the search; they count half as much as query words. " +
        "Narrow with memberType, pathGlob ('src/Billing/**'), matchIn (Names = symbol names only, Docs, Code = body words) and includeTests=false. " +
        "Workspace '*' searches ALL enabled workspaces. Interface also finds the base-list entries of types ('IUserService' finds its implementers); Attribute finds attribute use ('HttpPost'). " +
        "Examples: code_search('MyApi', 'Redis') | code_search('MyApi', 'refresh token rotate', terms=['jwt']) | code_search('*', 'Authorize', memberType='Attribute') | code_search('MyApi', 'retry -test', pathGlob='src/**'). " +
        "Results come from the syntax index (no symbol resolution). Body words are stored as single words in order of first use, never as lines of code, with secrets redacted first (values after names such as password, secret, token or apiKey are masked as well, but a secret written in another way can remain: keep credentials out of source). The match count shows '+' when the full-text index found more candidates than it ranks.")]
    public Task<string> Search(
        [Description("What to find: a type or member name, camel-case initials, or a description in a few words ('Redis', 'UserService', 'refresh token rotation'). Syntax: \"phrase\", +required, -excluded. At most 200 characters and 16 words (terms included); a query of only filler words (how, does, the) is rejected. Quoted phrases need adjacent words in a name, signature or doc; with matchIn=Code they mean 'all these words'.")]
        string query,
        [Description("Workspace name as shown in Waymark, or '*' for ALL enabled workspaces")]
        string workspace,
        [Description("Optional: All (default), Class, Interface, Struct, Record, RecordStruct, Enum, Delegate, TypeAlias, Method, Constructor, Property, Field, Event, Indexer, Operator, EnumMember, Function, Variable, Attribute")]
        string memberType = "All",
        [Description("Optional: case-sensitive name matching (default false). Doc and body words are always matched ignoring case.")]
        bool caseSensitive = false,
        [Description("Optional: results per page (default 20, max 200). Use with 'page' to paginate large result sets.")]
        int? pageSize = null,
        [Description("Optional: page number, 1-based (default 1). Increment to retrieve subsequent pages.")]
        int? page = null,
        [Description("Optional: extra words that widen the search - synonyms, other spellings, the vocabulary the code probably uses (['jwt','bearer']). Up to 16 words together with the query.")]
        string[]? terms = null,
        [Description("Optional: where to look. All (default) = names, doc comments, paths and body words; Names = symbol names only; Docs = doc comments only; Code = the words used in member bodies only")]
        string matchIn = "All",
        [Description("Optional: only files matching this glob, e.g. 'src/Billing/**', '**/*.py' or 'Program.cs' (a pattern without '/' matches the file name in any folder). Only '*', '**' and '?' are patterns; '[ ] { }' are rejected.")]
        string? pathGlob = null,
        [Description("Optional: include hits in test folders (default true; false leaves them out and reports how many)")]
        bool includeTests = true,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => search.SearchAsync(workspace, query, memberType, caseSensitive, pageSize, page, terms, matchIn, pathGlob, includeTests, cancellationToken));

    [McpServerTool(Name = "code_callers", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Shows WHO calls a method and WHERE exactly, and what the method itself calls. " +
        "CRITICAL for impact analysis. Returns caller locations (file, line, calling type and member, receiver) and the calls made inside the method. " +
        "Use this BEFORE: changing a method signature (prevents breaking callers), modifying behavior, deleting a method, or renaming a method (find all references). " +
        "Example: before changing GetUser(), verify which services depend on its parameter order. " +
        "Callers are found by method NAME in the syntax index, then sorted by the declared type of their receiver: calls on an unrelated type are left out and counted (includeUnrelated=true lists them), and '?' marks a call whose receiver type is unknown. All overloads share one result. " +
        "In TypeScript and JavaScript a local function (a handler or useCallback inside a React component) works too, and the calls inside it are reported under its own name, not its component's.")]
    public Task<string> Callers(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("Path of the source file (.cs, .py, .ts, .tsx, .js, .jsx and the like) that defines the method or function, relative to the workspace root (e.g. 'Services/UserService.cs')")]
        string relativeFilePath,
        [Description("Method or function name to analyze (e.g. 'GetUser', 'ProcessOrder'). A Python class name, or '__init__', finds the calls that create the class.")]
        string methodName,
        [Description("Optional: class name when the file has several classes with this method; in TypeScript and JavaScript also the function (component, hook) that declares a local function")]
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
