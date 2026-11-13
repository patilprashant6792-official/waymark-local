using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Waymark.Core.Security;
using Waymark.Modules.Code.Reading;

namespace Waymark.Modules.Code.Tools;

[McpServerToolType]
public sealed class CodeReadTools(ICodeReadService reader, ILogger<CodeReadTools> logger)
{
    [McpServerTool(Name = "code_outline", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "PREFERRED METHOD: Analyzes source file(s) (C#, Python, TypeScript and JavaScript, including .tsx and .jsx) and returns structured metadata WITHOUT loading full content. " +
        "Returns: namespace (module in Python and TypeScript), using directives (imports), types (classes, interfaces, records, enums, type aliases...), their members with signatures, attributes (decorators) and line ranges, module-level functions and variables, and the file classification. " +
        "In TypeScript and JavaScript the functions declared inside a function (a React component's handlers and useCallback/useMemo values, a hook's helpers) are listed indented under it as local members. " +
        "BATCH MODE: pass 'File1.cs,File2.py,File3.tsx' (comma-separated, NO SPACES) to analyze several files in one call, of any language. " +
        "Use this BEFORE code_method to understand the structure. " +
        "TOKEN-EFFICIENT: always prefer this for C#, Python, TypeScript and JavaScript files; use code_read for other files. " +
        "Set includePrivateMembers=true when debugging internals, analyzing dependency injection (constructors), refactoring private methods or needing the full class architecture. " +
        "Default: false (public API surface only). The file is parsed from disk on every call, so the result is never stale.")]
    public Task<string> Outline(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description(
            "Path(s) of source file(s) (.cs, .py, .pyi, .ts, .tsx, .js, .jsx, .mts, .cts, .mjs, .cjs) relative to the workspace root. " +
            "Single: 'Services/UserService.cs' | " +
            "Batch: 'Services/UserService.cs,app/models/user.py,src/components/Table.tsx' (NO SPACES), any mix of languages")]
        string relativeFilePath,
        [Description("Optional: include non-public members (default false, public only)")]
        bool includePrivateMembers = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => reader.OutlineAsync(workspace, relativeFilePath, includePrivateMembers, cancellationToken));

    [McpServerTool(Name = "code_method", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "PREFERRED METHOD: Fetches the complete source of method(s) (also constructors, properties, module-level functions, the local functions of a TypeScript or JavaScript function such as a React component's handlers, and other members) with line numbers. " +
        "Returns: kind, class or declaring function, namespace, line range, return type, modifiers, doc summary (XML comment, docstring or JSDoc) and the full body including attributes or decorators. " +
        "A member too large for one answer (a big React component) comes back as a map instead: its local members with line ranges and its opening lines; fetch the parts you need by name. " +
        "BATCH MODE: pass 'Method1,Method2,Method3' (comma-separated, NO SPACES) to fetch several members in one call. " +
        "Overloads of the same name are all returned. " +
        "Use AFTER code_outline to drill into specific members. " +
        "CRITICAL: the line numbers are the real file lines, so they can be used directly with file_edit_lines.")]
    public Task<string> Method(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("Path of the source file relative to the workspace root (e.g. 'Services/UserService.cs', 'app/services/user.py' or 'src/components/TradeTable.tsx')")]
        string relativeFilePath,
        [Description("Member name(s) to fetch. Single: 'GetUsers' | Batch: 'GetUsers,UpdateUser,DeleteUser' (NO SPACES)")]
        string methodName,
        [Description("Optional: class name when the file has several classes with the same member name; in TypeScript and JavaScript also the name of the function (component, hook) that declares a local member")]
        string? className = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => reader.MethodsAsync(workspace, relativeFilePath, methodName, className, cancellationToken));

    [McpServerTool(Name = "code_read", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "LAST RESORT: Reads raw file content for source code and safe configuration files.\n" +
        "SECURITY: sensitive files (appsettings*.json, secrets, credentials, keys, .env, database files) and build output are refused.\n" +
        "DECISION RULE: use ONLY for files of about 15 KB or less, or for files the code tools cannot outline (Dockerfile, launchSettings.json, project files, pyproject.toml, .razor).\n" +
        "For larger C#, Python, TypeScript or JavaScript files use code_outline + code_method instead (large token savings).\n\n" +
        "MODES (mutually exclusive, pick one):\n" +
        "  - Full file: omit all optional parameters\n" +
        "  - Line range: provide startLine + endLine to fetch a slice (1-based, inclusive)\n" +
        "  - Search: provide query to return all lines containing it, with line numbers (case-insensitive)\n\n" +
        "EXAMPLES:\n" +
        "  code_read(workspace, 'Program.cs')                           - full file\n" +
        "  code_read(workspace, 'Program.cs', startLine=10, endLine=40) - lines 10-40 only\n" +
        "  code_read(workspace, 'Program.cs', query='AddSingleton')     - all lines containing 'AddSingleton'\n\n" +
        "RETURNS: numbered lines.")]
    public Task<string> Read(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("File path relative to the workspace root (e.g. 'Program.cs', 'Dockerfile')")]
        string relativeFilePath,
        [Description("Optional (range mode): first line to return, 1-based inclusive. Must be paired with endLine.")]
        int? startLine = null,
        [Description("Optional (range mode): last line to return, 1-based inclusive. Must be paired with startLine.")]
        int? endLine = null,
        [Description("Optional (search mode): case-insensitive text. Returns only lines that contain it. Mutually exclusive with startLine/endLine.")]
        string? query = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => reader.ReadAsync(workspace, relativeFilePath, startLine, endLine, query, cancellationToken));

    private async Task<string> RunAsync(Func<Task<string>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileAccessDeniedException or ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            throw new McpException(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "A code reading tool could not read a file.");
            throw new McpException("The file could not be read. See the Waymark log for details.");
        }
    }
}
