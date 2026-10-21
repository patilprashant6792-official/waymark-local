using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Waymark.Core.Tools;

namespace Waymark.Modules.Files.Tools;

[McpServerToolType]
public sealed class FileWriteTool(IFileEditor editor, ToolPipelineOptions pipeline)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    [McpServerTool(Name = "file_write", ReadOnly = false, Destructive = true, Idempotent = true)]
    [ToolGate(ToolGate.Confirm)]
    [Description(
        "Create or overwrite one or more files. Each entry has its own mode:\n" +
        "  - create: fails if the file already exists (safe default for new files)\n" +
        "  - overwrite: fails if the file does NOT exist (explicit replacement)\n" +
        "  - upsert: create or overwrite unconditionally\n" +
        "Parent folders are created automatically. An existing file keeps its encoding and line endings; new files are UTF-8.\n" +
        "BATCH: pass several entries to scaffold a feature in one call.\n" +
        "WHEN TO USE: new files or full rewrites. For targeted edits use file_edit_lines.\n" +
        "RETURNS: per-file success or error, line count, size in bytes and last-modified time.")]
    public async Task<string> WriteFiles(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description(
            "JSON array of files to write. Each entry:\n" +
            "  { \"relativeFilePath\": \"Services/OrderService.cs\",\n" +
            "    \"content\": \"<full file content>\",\n" +
            "    \"mode\": \"create|overwrite|upsert\" }\n" +
            "mode defaults to upsert when omitted.")]
        string filesJson,
        CancellationToken cancellationToken = default)
    {
        var files = ParseFiles(filesJson);
        IReadOnlyList<FileWriteResult> results;
        try
        {
            results = await editor.WriteAsync(workspace, files, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            throw new McpException(ex.Message);
        }

        return Format(results);
    }

    private List<WriteFileEntry> ParseFiles(string? filesJson)
    {
        if (string.IsNullOrWhiteSpace(filesJson))
        {
            throw new McpException("filesJson is required: a JSON array of files to write.");
        }

        try
        {
            var files = JsonSerializer.Deserialize<List<WriteFileEntry>>(filesJson, JsonOptions);
            return files is { Count: > 0 } && files.TrueForAll(f => f is not null)
                ? files
                : throw new McpException("filesJson must be a non-empty JSON array of file entries.");
        }
        catch (JsonException ex)
        {
            throw new McpException($"filesJson is not valid: {ex.Message}");
        }
    }

    private string Format(IReadOnlyList<FileWriteResult> results)
    {
        var succeeded = results.Count(r => r.Success);
        var failed = results.Count - succeeded;
        var output = new OutputBudget(pipeline.MaxOutputChars);
        output.TryAppendLine(failed == 0
            ? $"✅ file_write - {succeeded} succeeded"
            : $"⚠️ file_write - {succeeded} succeeded, {failed} failed");
        foreach (var r in results)
        {
            output.TryAppendLine(r.Success
                ? $"   ✅ {r.RelativeFilePath} ({r.LineCount} lines, {r.SizeBytes} bytes, modified: {r.ModifiedUtc?.ToString("O", CultureInfo.InvariantCulture)})"
                : $"   ❌ {r.RelativeFilePath} - {r.Error}");
        }

        return output.Build().TrimEnd();
    }
}
