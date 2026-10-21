using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Waymark.Core.Security;
using Waymark.Modules.Code.Content;

namespace Waymark.Modules.Code.Tools;

[McpServerToolType]
public sealed class CodeGrepTools(ITextSearchService grep)
{
    [McpServerTool(Name = "code_grep", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Finds literal TEXT in any readable file of a workspace, not only C#: config keys, routes, log messages, SQL, JSON, YAML, Razor, scripts, markdown. " +
        "WILDCARD SUPPORT: pass workspace='*' to search ALL enabled workspaces at once. " +
        "Files are read from disk on every call, so results are never stale and every line number is the real line (valid for code_read and file_edit_lines until the file changes). " +
        "Matching is plain text on one line (no regex), any letter case unless caseSensitive=true. Results come in path order with the matching lines. " +
        "Use code_search for C# type and member NAMES (ranked, forgiving); use code_grep when the text is not a name. " +
        "Not searched: files the access policy blocks, vendored folders (wwwroot/lib, dist, vendor), *.min.* and minified files, binary files and files over about 1 MB; the result says how many were left out. " +
        "Examples: code_grep('MyApi', 'ConnectionStrings') | code_grep('*', 'api/orders', pathGlob='**/*.cs') | code_grep('MyApi', 'TODO', pathGlob='src/Orders/**').")]
    public Task<string> Grep(
        [Description("Workspace name as shown in Waymark, or '*' for ALL enabled workspaces")]
        string workspace,
        [Description("Literal text to find on one line (not a pattern, no line breaks)")]
        string query,
        [Description("Optional: limit to workspace-relative paths. '*' stays inside a folder, '**' crosses folders, '?' is one character. Without '/' it matches the file name in any folder: '*.json', 'src/Orders/**', '**/appsettings*.yml'")]
        string? pathGlob = null,
        [Description("Optional: case-sensitive match (default false)")]
        bool caseSensitive = false,
        [Description("Optional: matching lines per page (default 50, max 200). Use with 'page' to read further.")]
        int? pageSize = null,
        [Description("Optional: page number, 1-based (default 1).")]
        int? page = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => grep.GrepAsync(workspace, query, caseSensitive, pathGlob, pageSize, page, cancellationToken));

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
