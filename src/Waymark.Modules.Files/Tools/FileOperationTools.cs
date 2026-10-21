using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Waymark.Core.Tools;

namespace Waymark.Modules.Files.Tools;

[McpServerToolType]
public sealed class FileOperationTools(IFileManager manager, ToolPipelineOptions pipeline)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [McpServerTool(Name = "folder_create", ReadOnly = false, Destructive = false, Idempotent = true)]
    [ToolGate(ToolGate.Confirm)]
    [Description(
        "Create one or more folders (including nested paths) in one call.\n" +
        "Idempotent - already existing folders are not an error.\n" +
        "EXAMPLE: [\"Features/Orders\", \"Features/Orders/Models\", \"Features/Orders/Services\"]\n" +
        "RETURNS: per-folder success or error.")]
    public async Task<string> CreateFolder(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("JSON array of relative folder paths to create:\n  [\"Features/Orders\", \"Features/Orders/Models\"]")]
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
        "Move or rename one or more files within the same workspace.\n" +
        "Same-folder destination = rename. Cross-folder destination = move.\n" +
        "ATOMIC SAFETY: ALL entries are validated before ANY file is moved.\n" +
        "If one entry fails validation the whole batch is aborted - nothing is moved.\n" +
        "An existing destination is never overwritten. Namespaces inside moved C# files are not updated.\n" +
        "RETURNS: per-file success or error.")]
    public async Task<string> MoveFile(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("JSON array of move entries:\n  [ { \"from\": \"Services/OldName.cs\", \"to\": \"Core/Services/NewName.cs\" } ]")]
        string movesJson,
        CancellationToken cancellationToken = default)
    {
        var moves = Parse<MoveEntry>(movesJson, "movesJson");
        var results = await RunAsync(() => manager.MoveFilesAsync(workspace, moves, cancellationToken)).ConfigureAwait(false);
        return Format("file_move", results);
    }

    [McpServerTool(Name = "file_delete", ReadOnly = false, Destructive = true, Idempotent = false)]
    [ToolGate(ToolGate.Confirm)]
    [Description(
        "Delete one or more files. Each file is independent - partial success is possible.\n" +
        "UNDO: use git. No backups are created.\n" +
        "BLOCKED: files in restricted folders (bin, obj, .git, ...) and files that hold secrets, keys or databases.\n" +
        "RETURNS: per-file success or error.")]
    public async Task<string> DeleteFile(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("JSON array of relative file paths to delete:\n  [\"Services/OldService.cs\", \"Services/IOldService.cs\"]")]
        string pathsJson,
        CancellationToken cancellationToken = default)
    {
        var paths = Parse<string?>(pathsJson, "pathsJson");
        var results = await RunAsync(() => manager.DeleteFilesAsync(workspace, paths, cancellationToken)).ConfigureAwait(false);
        return Format("file_delete", results);
    }

    [McpServerTool(Name = "folder_move", ReadOnly = false, Destructive = false, Idempotent = false)]
    [ToolGate(ToolGate.Confirm)]
    [Description(
        "Move or rename a folder and its entire contents.\n" +
        "Intentionally single-operation - folder moves are high-impact and should be deliberate.\n" +
        "Same-parent destination = rename. Cross-parent = move. An existing destination is never overwritten.\n" +
        "NOTE: namespaces inside moved C# files are not updated. The code index follows the move automatically.\n" +
        "RETURNS: success or error.")]
    public async Task<string> MoveFolder(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("Current relative folder path (e.g. 'Services')")]
        string from,
        [Description("New relative folder path (e.g. 'Core/Services')")]
        string to,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(async () => (IReadOnlyList<FileOperationResult>)[await manager.MoveFolderAsync(workspace, from, to, cancellationToken).ConfigureAwait(false)]).ConfigureAwait(false);
        return Format("folder_move", result);
    }

    [McpServerTool(Name = "folder_delete", ReadOnly = false, Destructive = true, Idempotent = false)]
    [ToolGate(ToolGate.Confirm)]
    [Description(
        "Delete one or more folders recursively.\n" +
        "Folders are deleted deepest-first - safe to include both parent and child in one call.\n" +
        "A folder that does not exist is skipped (not an error). A folder that contains a link is refused.\n" +
        "BLOCKED: bin, obj, .git, .vs, .ssh, node_modules, backups, logs, packages and other restricted folders.\n" +
        "UNDO: use git. No backups are created.\n" +
        "RETURNS: per-folder success or error.")]
    public async Task<string> DeleteFolder(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("JSON array of relative folder paths to delete:\n  [\"Features/OldFeature\", \"Features/OldFeature/Models\"]")]
        string pathsJson,
        CancellationToken cancellationToken = default)
    {
        var paths = Parse<string?>(pathsJson, "pathsJson");
        var results = await RunAsync(() => manager.DeleteFoldersAsync(workspace, paths, cancellationToken)).ConfigureAwait(false);
        return Format("folder_delete", results);
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
