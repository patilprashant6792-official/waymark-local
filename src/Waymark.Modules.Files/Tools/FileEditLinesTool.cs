using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Waymark.Core.Tools;

namespace Waymark.Modules.Files.Tools;

[McpServerToolType]
public sealed class FileEditLinesTool(IFileEditor editor, ToolPipelineOptions pipeline)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    [McpServerTool(Name = "file_edit_lines", ReadOnly = false, Destructive = true, Idempotent = false)]
    [ToolGate(ToolGate.Confirm)]
    [Description(
        "Apply one or more edits to a single file in one atomic operation.\n" +
        "All patches are validated and then applied bottom-up - supply the ORIGINAL line numbers from your last read.\n\n" +
        "actions:\n" +
        "  - patch: replace startLine..endLine with content (both inclusive, 1-based)\n" +
        "  - insert: insert content after startLine; use startLine=0 to prepend\n" +
        "  - delete: remove startLine..endLine; content is ignored\n" +
        "  - append: add content at the end of the file; line numbers are ignored\n" +
        "Content is lines separated by line breaks; one final line break does not add an empty line.\n\n" +
        "MANDATORY BATCHING RULE - violations cause wrong-place edits:\n" +
        "  When making several changes to the same file, send ALL patches in a SINGLE file_edit_lines call.\n" +
        "  Waymark applies them bottom-up, so line numbers stay correct across all patches. A second call uses\n" +
        "  line numbers that changed with the first one and lands at the WRONG LINE.\n" +
        "  Correct: one call with patches [{lines 10-12}, {lines 45-50}, {lines 80-85}]\n" +
        "  Wrong: three separate file_edit_lines calls for the same file.\n\n" +
        "OVERLAP RULE: ranges must not overlap - this is checked before anything is written.\n" +
        "The file keeps its encoding and line endings.\n" +
        "RETURNS: final line count, size in bytes, last-modified time and lines affected.")]
    public async Task<string> EditLines(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("Relative file path from the workspace root (e.g. 'Services/OrderService.cs')")]
        string relativeFilePath,
        [Description(
            "JSON array of patch operations. Each entry:\n" +
            "  { \"action\": \"patch|insert|delete|append\",\n" +
            "    \"startLine\": 45,\n" +
            "    \"endLine\": 89,\n" +
            "    \"content\": \"<new content>\" }\n" +
            "startLine/endLine are 1-based. content is ignored for delete.")]
        string patchesJson,
        CancellationToken cancellationToken = default)
    {
        var patches = ParsePatches(patchesJson);
        FileWriteResult result;
        try
        {
            result = await editor.EditLinesAsync(workspace, relativeFilePath, patches, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            throw new McpException(ex.Message);
        }

        return Format(result);
    }

    private static List<PatchOperation> ParsePatches(string? patchesJson)
    {
        if (string.IsNullOrWhiteSpace(patchesJson))
        {
            throw new McpException("patchesJson is required: a JSON array of patch operations.");
        }

        try
        {
            var patches = JsonSerializer.Deserialize<List<PatchOperation>>(patchesJson, JsonOptions);
            return patches is { Count: > 0 } && patches.TrueForAll(p => p is not null)
                ? patches
                : throw new McpException("patchesJson must be a non-empty JSON array of patch operations.");
        }
        catch (JsonException ex)
        {
            throw new McpException($"patchesJson is not valid: {ex.Message}");
        }
    }

    private string Format(FileWriteResult result)
    {
        var output = new OutputBudget(pipeline.MaxOutputChars);
        if (result.Success)
        {
            output.TryAppendLine("✅ file_edit_lines - 1 succeeded");
            output.TryAppendLine($"   ✅ {result.RelativeFilePath} ({result.LineCount} lines, {result.SizeBytes} bytes, {result.LinesAffected} lines affected, modified: {result.ModifiedUtc?.ToString("O", CultureInfo.InvariantCulture)})");
        }
        else
        {
            output.TryAppendLine("⚠️ file_edit_lines - 1 failed, file unchanged");
            output.TryAppendLine($"   ❌ {result.RelativeFilePath} - {result.Error}");
        }

        return output.Build().TrimEnd();
    }
}
