using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Waymark.Core.Memory;
using Waymark.Core.Tools;

namespace Waymark.Modules.Files.Tools;

[McpServerToolType]
public sealed class FileOperationTools(IFileManager manager, ToolPipelineOptions pipeline, IFlowEditAdvisor advisor)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [McpServerTool(Name = "folder_create", ReadOnly = false, Destructive = false, Idempotent = true)]
    [ToolGate(ToolGate.Confirm)]
    [Description(
        "Creates one or more empty folders in a workspace, with any missing parent folders, in one call. A folder that already exists is reported as such, not as an error.\n" +
        "WHEN TO USE: only for folders that must exist before any file is in them (a layout to fill later, an output folder a tool expects). " +
        "file_write and file_move already create the parent folders they need, so do not call this first. To rename or move a folder use folder_move; to remove one use folder_delete.\n" +
        "Each path is handled on its own: a path that is already a file, outside the workspace or blocked by the access rules fails alone and the others are still created. At most 50 paths per call.\n" +
        "RETURNS: one line per path: created, already existed, or the error.")]
    public async Task<string> CreateFolder(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("JSON array of folder paths relative to the workspace root, '/' as separator. A nested path creates every level, so [\"Features/Orders/Models\"] alone is enough for three folders.")]
        string pathsJson,
        CancellationToken cancellationToken = default)
    {
        var paths = Parse<string?>(pathsJson, "pathsJson");
        var results = await RunAsync(() => manager.CreateFoldersAsync(workspace, paths, cancellationToken)).ConfigureAwait(false);
        return Format("folder_create", results);
    }

    [McpServerTool(Name = "file_move", ReadOnly = false, Destructive = false, Idempotent = false)]
    [ToolGate(ToolGate.Confirm)]
    [Description(
        "Moves or renames one or more files inside one workspace. A destination in the same folder renames; one in another folder moves, creating missing destination folders.\n" +
        "ALL OR NOTHING: every entry is checked first (source exists and is a file, destination free, no two entries to the same place, access rules). If any entry fails, nothing is moved and each problem is listed.\n" +
        "An existing destination is never overwritten: delete it first with file_delete if it must be replaced. At most 50 entries per call.\n" +
        "Contents are not changed: C# namespaces, imports and references to the old path stay as they were, so fix them afterwards (file_replace) and build. " +
        "Saved flows whose steps lived in a moved file are named in the reply, to update with memorise.\n" +
        "WHEN TO USE: renaming or reorganising files. For a whole folder use folder_move; to copy a file use file_write with a copy entry.\n" +
        "RETURNS: one line per file with its new path, or the reasons nothing was moved.")]
    public async Task<string> MoveFile(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("JSON array of moves, paths relative to the workspace root: [ { \"from\": \"Services/OldName.cs\", \"to\": \"Core/Services/NewName.cs\" } ]. 'to' is the full new file path, not a folder.")]
        string movesJson,
        CancellationToken cancellationToken = default)
    {
        var moves = Parse<MoveEntry>(movesJson, "movesJson");
        var results = await RunAsync(() => manager.MoveFilesAsync(workspace, moves, cancellationToken)).ConfigureAwait(false);
        return await WithAdviceAsync(workspace, Format("file_move", results), results, FlowEditKind.Moved, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "file_delete", ReadOnly = false, Destructive = true, Idempotent = false)]
    [ToolGate(ToolGate.Confirm)]
    [Description(
        "Deletes one or more files from a workspace for good: no backup and no recycle bin; only git can bring back a tracked file.\n" +
        "Each file is handled on its own, so some can succeed while others fail. A missing file is an error for that entry; a folder path is refused (use folder_delete). At most 50 paths per call.\n" +
        "BLOCKED: files in restricted folders (bin, obj, .git and the like), files that hold secrets, keys or databases, and anything the workspace's access rules hide.\n" +
        "Saved flows whose steps lived in a deleted file are named in the reply, to update with memorise.\n" +
        "WHEN TO USE: removing files that are no longer needed, after code_callers or code_grep shows nothing still uses them. To rename or move instead use file_move; to empty a file use file_write.\n" +
        "RETURNS: one line per file: deleted, or the error.")]
    public async Task<string> DeleteFile(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("JSON array of file paths relative to the workspace root: [\"Services/OldService.cs\", \"Services/IOldService.cs\"]. Files only, no folders or wildcards.")]
        string pathsJson,
        CancellationToken cancellationToken = default)
    {
        var paths = Parse<string?>(pathsJson, "pathsJson");
        var results = await RunAsync(() => manager.DeleteFilesAsync(workspace, paths, cancellationToken)).ConfigureAwait(false);
        return await WithAdviceAsync(workspace, Format("file_delete", results), results, FlowEditKind.Deleted, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "folder_move", ReadOnly = false, Destructive = false, Idempotent = false)]
    [ToolGate(ToolGate.Confirm)]
    [Description(
        "Moves or renames one folder with everything in it, inside one workspace. Same parent folder = rename; another parent = move (missing parent folders are created).\n" +
        "One folder per call on purpose: a folder move touches many files at once. The destination must not exist (it is never overwritten or merged), a folder cannot move into itself, " +
        "and a change of letter case only is allowed as a rename.\n" +
        "Contents are not changed: C# namespaces, imports and project references to the old path stay as they were, so fix them afterwards (file_replace) and build. The code index follows the move.\n" +
        "WHEN TO USE: reorganising a feature or project folder. For single files use file_move (it takes many at once); to remove a folder use folder_delete.\n" +
        "RETURNS: the new path, or the error.")]
    public async Task<string> MoveFolder(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("The folder to move, relative to the workspace root (e.g. 'Services'). Must exist.")]
        string from,
        [Description("The full new folder path, relative to the workspace root (e.g. 'Core/Services'), not the parent to move into. Must not exist.")]
        string to,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(async () => (IReadOnlyList<FileOperationResult>)[await manager.MoveFolderAsync(workspace, from, to, cancellationToken).ConfigureAwait(false)]).ConfigureAwait(false);
        return Format("folder_move", result);
    }

    [McpServerTool(Name = "folder_delete", ReadOnly = false, Destructive = true, Idempotent = false)]
    [ToolGate(ToolGate.Confirm)]
    [Description(
        "Deletes one or more folders with everything in them, for good: no backup and no recycle bin; only git can bring back tracked files.\n" +
        "Folders are deleted deepest first, so a parent and its child may be in one call. A folder that is already gone is reported as such, not as an error; " +
        "a file path is refused (use file_delete), and so is the workspace root. At most 50 paths per call.\n" +
        "REFUSED: a folder that contains a link (it could reach outside the workspace), a folder with more than 20,000 entries (delete its subfolders one at a time), " +
        "and restricted folders: bin, obj, .git, .vs, .ssh, node_modules, backups, logs, packages and anything the workspace's access rules hide.\n" +
        "WHEN TO USE: removing a feature, test or sample folder that is no longer needed. To keep the contents under another name use folder_move; for single files use file_delete.\n" +
        "RETURNS: one line per folder: deleted, already gone, or the error.")]
    public async Task<string> DeleteFolder(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("JSON array of folder paths relative to the workspace root: [\"Features/OldFeature\", \"Features/OldFeature/Models\"]. Folders only, no wildcards.")]
        string pathsJson,
        CancellationToken cancellationToken = default)
    {
        var paths = Parse<string?>(pathsJson, "pathsJson");
        var results = await RunAsync(() => manager.DeleteFoldersAsync(workspace, paths, cancellationToken)).ConfigureAwait(false);
        return Format("folder_delete", results);
    }

    // A moved or deleted file takes its steps out of the flows that name it: tell the agent which flows, once, after the result. The paths are the ones the operation took (the old place of a move).
    private async Task<string> WithAdviceAsync(string workspace, string text, IReadOnlyList<FileOperationResult> results, FlowEditKind kind, CancellationToken ct)
    {
        var paths = results.Where(r => r.Success).Select(r => r.Path).ToList();
        var advice = await advisor.AdviseAsync(workspace, paths, kind, ct).ConfigureAwait(false);
        return advice is null ? text : $"{text}\n{advice}";
    }

    private static async Task<IReadOnlyList<FileOperationResult>> RunAsync(Func<Task<IReadOnlyList<FileOperationResult>>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            throw new McpException(ex.Message);
        }
    }

    private static List<T> Parse<T>(string? json, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new McpException($"{argumentName} is required: a JSON array.");
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<T>>(json, JsonOptions);
            return items is { Count: > 0 }
                ? items
                : throw new McpException($"{argumentName} must be a non-empty JSON array.");
        }
        catch (JsonException ex)
        {
            throw new McpException($"{argumentName} is not valid: {ex.Message}");
        }
    }

    private string Format(string tool, IReadOnlyList<FileOperationResult> results)
    {
        var succeeded = results.Count(r => r.Success);
        var failed = results.Count - succeeded;
        var output = new OutputBudget(pipeline.MaxOutputChars);
        output.TryAppendLine(failed == 0
            ? $"✅ {tool} - {succeeded} succeeded"
            : $"⚠️ {tool} - {succeeded} succeeded, {failed} failed");
        foreach (var r in results)
        {
            output.TryAppendLine(r.Success
                ? $"   ✅ {r.Path}{(r.Note is null ? string.Empty : $" ({r.Note})")}"
                : $"   ❌ {r.Path} - {r.Error}");
        }

        return output.Build().TrimEnd();
    }
}
