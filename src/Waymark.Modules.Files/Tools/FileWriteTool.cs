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
        "COPY, DO NOT RETYPE: an entry may carry \"from\" instead of content. The server copies a whole file or a line range of a file (any enabled workspace), so you write none of it.\n" +
        "  \"from\": { \"workspace\": \"Legacy\" (default: this workspace), \"file\": \"Services/A.cs\" (required),\n" +
        "            \"startLine\": 40, \"endLine\": 80 (leave both out for the whole file),\n" +
        "            \"expectFirstLine\" / \"expectLastLine\": (trimmed; the copy is refused if the lines moved),\n" +
        "            \"replace\": [{\"find\": \"Invoice\", \"with\": \"Order\", \"count\": 12}] (literal text, the match count must equal count),\n" +
        "            \"csharp\": {\"namespaces\": {\"Old.Ns\": \"New.Ns\"}, \"identifiers\": {\"OldName\": \"NewName\"}} (C# files only, by syntax; strings and comments are never touched; use replace for other languages),\n" +
        "            \"indent\": -4 (shift every line) }\n" +
        "A copied new file keeps the encoding and line endings of its source. Use it to duplicate, migrate or split files; use mode create so nothing is overwritten by accident.\n" +
        "Copied text that holds a secret is refused. Run the build after a copy: the build is the proof.\n" +
        "BULK (migrations): an entry {\"fromFolder\": {\"workspace\": \"Legacy\", \"folder\": \"src/Billing\", \"toFolder\": \"src/Orders\", \"glob\": \"**/*.cs\" (below folder, optional),\n" +
        "            \"pathRename\": [{\"find\": \"Invoice\", \"with\": \"Order\"}] (literal, on the path below folder), \"replace\" / \"csharp\" / \"indent\": as above, applied to every file,\n" +
        "            \"mode\": \"create\" (default), \"confirmCount\": 37}} copies every file of a folder tree (at most 500 per call). Blocked folders, links and refused files are left out.\n" +
        "  Always run it with dryRun=true first: it lists every file and the count; then repeat without dryRun and pass that count as confirmCount (a changed tree is refused).\n" +
        "  A file that fails (binary, a secret, an existing target) fails alone; the rest are copied. Keep the result as the migration record.\n" +
        "dryRun=true runs every check and copy and reports what would happen without writing.\n" +
        "WHEN TO USE: new files or full rewrites. For targeted edits use file_edit_lines.\n" +
        "RETURNS: per-file success or error, line count, size in bytes, last-modified time and, for copies, where the text came from.")]
    public async Task<string> WriteFiles(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description(
            "JSON array of files to write. Each entry:\n" +
            "  { \"relativeFilePath\": \"Services/OrderService.cs\",\n" +
            "    \"content\": \"<full file content>\",\n" +
            "    \"mode\": \"create|overwrite|upsert\" }\n" +
            "or, to copy instead of typing, replace content by \"from\": { ... } (see the tool description).\n" +
            "mode defaults to upsert when omitted.")]
        string filesJson,
        [Description("true: check and report only, write nothing (default false)")]
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var files = ParseFiles(filesJson);
        IReadOnlyList<FileWriteResult> results;
        try
        {
            results = await editor.WriteAsync(workspace, files, dryRun, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            throw new McpException(ex.Message);
        }

        return Format(results, dryRun);
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

    private string Format(IReadOnlyList<FileWriteResult> results, bool dryRun)
    {
        // A result without a line count describes a whole folder copy; it is shown but not counted as a file.
        var files = results.Where(r => !(r.Success && r.LineCount is null)).ToList();
        var succeeded = files.Count(r => r.Success);
        var failed = files.Count - succeeded;
        var output = new OutputBudget(pipeline.MaxOutputChars);
        var verb = dryRun ? "file_write dry run (nothing was written)" : "file_write";
        output.TryAppendLine(failed == 0
            ? $"{(dryRun ? "🔍" : "✅")} {verb} - {succeeded} succeeded"
            : $"⚠️ {verb} - {succeeded} succeeded, {failed} failed");
        foreach (var r in results)
        {
            if (!r.Success)
            {
                output.TryAppendLine($"   ❌ {r.RelativeFilePath} - {r.Error}");
                continue;
            }

            var modified = r.ModifiedUtc is { } time ? $", modified: {time.ToString("O", CultureInfo.InvariantCulture)}" : string.Empty;
            output.TryAppendLine(r.LineCount is null
                ? $"   📁 {r.RelativeFilePath}"
                : $"   ✅ {r.RelativeFilePath} ({r.LineCount} lines, {r.SizeBytes} bytes{modified})");
            if (r.Note is not null)
            {
                output.TryAppendLine($"      copy: {r.Note}");
            }
        }

        return output.Build().TrimEnd();
    }
}
