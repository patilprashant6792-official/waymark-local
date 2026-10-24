using System.Globalization;
using System.Text;
using Waymark.Core.Code;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Files;

/// <summary>
/// Turns a <see cref="CopySource"/> into text: finds the source file through the path guard (any enabled workspace), checks that the lines are still the ones the caller saw,
/// applies the transforms, and refuses text that holds a secret. Nothing is written here.
/// </summary>
internal sealed class CopySourceReader(IPathGuard guard, IContentInspector inspector, FilesOptions options, ICSharpRewriter? rewriter = null)
{
    private const int ShownCharacters = 100;

    /// <summary>Registers a file the caller has already read, so it is not read again and every part of the call sees the same bytes.</summary>
    public static void Seed(CopySession session, Workspace workspace, GuardedPath path, byte[] bytes, TextFileFormat format, string text) =>
        session.Snapshots[path.AbsolutePath] = new SourceSnapshot(workspace, path, bytes, format, text);

    /// <exception cref="PatchValidationException">The source is missing, unreadable, binary, stale, over a limit, or holds a secret.</exception>
    /// <exception cref="FileAccessDeniedException">The path guard refuses the source.</exception>
    public async Task<CopyPiece> ReadAsync(CopySession session, Workspace callWorkspace, string? defaultFile, CopySource source, bool keepWholeFileExact, CancellationToken ct)
    {
        var sourceWorkspace = await ResolveWorkspaceAsync(callWorkspace, source.Workspace, ct).ConfigureAwait(false);
        var file = string.IsNullOrWhiteSpace(source.File) ? defaultFile : source.File;
        if (file is null || (string.IsNullOrWhiteSpace(source.File) && sourceWorkspace.Id != callWorkspace.Id))
        {
            throw new PatchValidationException("from.file is required.");
        }

        var snapshot = await LoadAsync(session, sourceWorkspace, file, ct).ConfigureAwait(false);
        var display = $"{sourceWorkspace.Name}:{snapshot.Path.RelativePath}";
        var document = LineDocument.Parse(snapshot.Text);
        var whole = source.StartLine == 0 && source.EndLine == 0;
        if (source.Remove && whole)
        {
            throw new PatchValidationException("from.remove needs startLine and endLine; use file_move to move a whole file.");
        }

        var (start, end) = whole ? (1, document.Lines.Count) : ValidateRange(source, document.Lines.Count, display);
        if (document.Lines.Count == 0 && !keepWholeFileExact)
        {
            throw new PatchValidationException($"The source {display} is empty.");
        }

        CheckExpectations(source, document.Lines, start, end, display);
        var text = whole && keepWholeFileExact
            ? snapshot.Text.Replace("\r\n", "\n", StringComparison.Ordinal)
            : LinesToText(document.Lines.GetRange(start - 1, end - start + 1));
        var notes = new List<string>();
        text = Transform(text, source, notes);
        await RejectSecretsAsync(text, display, ct).ConfigureAwait(false);

        var lineCount = CountLines(text);
        var summary = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"{lineCount} line(s) from {display}")
            .Append(whole ? " (whole file)" : string.Create(CultureInfo.InvariantCulture, $" lines {start}-{end}"));
        foreach (var note in notes)
        {
            summary.Append(", ").Append(note);
        }

        return new CopyPiece(text, snapshot, start, end, source.Remove, summary.ToString());
    }

    /// <summary>Reads (once per call) and decodes a source file.</summary>
    public async Task<SourceSnapshot> LoadAsync(CopySession session, Workspace workspace, string relativePath, CancellationToken ct)
    {
        var path = guard.Resolve(workspace, relativePath);
        if (session.Snapshots.TryGetValue(path.AbsolutePath, out var cached))
        {
            return cached;
        }

        if (path.RelativePath.Length == 0 || Directory.Exists(path.AbsolutePath))
        {
            throw new PatchValidationException($"The source '{relativePath}' is a folder; give a file.");
        }

        LinkGuard.EnsureNoLinks(workspace.Path, path.RelativePath);
        var info = new FileInfo(path.AbsolutePath);
        if (!info.Exists)
        {
            throw new PatchValidationException($"The source file '{workspace.Name}:{path.RelativePath}' does not exist.");
        }

        if (info.Length > options.MaxCopySourceBytes)
        {
            throw new PatchValidationException($"The source file '{workspace.Name}:{path.RelativePath}' is larger than the copy limit of {options.MaxCopySourceBytes} bytes.");
        }

        var bytes = await File.ReadAllBytesAsync(path.AbsolutePath, ct).ConfigureAwait(false);
        var format = TextFileFormat.Detect(bytes);
        string text;
        try
        {
            text = format.DecodeStrict(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new PatchValidationException($"The source file '{workspace.Name}:{path.RelativePath}' is not valid text (it may be binary); Waymark will not copy it.");
        }

        var snapshot = new SourceSnapshot(workspace, path, bytes, format, text);
        session.Snapshots[path.AbsolutePath] = snapshot;
        return snapshot;
    }

    /// <summary>The workspace a source or source folder lives in: the call's own workspace unless another enabled one is named.</summary>
    public async Task<Workspace> ResolveWorkspaceAsync(Workspace callWorkspace, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), callWorkspace.Name, StringComparison.OrdinalIgnoreCase))
        {
            return callWorkspace;
        }

        try
        {
            return await guard.GetWorkspaceAsync(name.Trim(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            throw new PatchValidationException($"from.workspace: {ex.Message}");
        }
    }

    private static (int Start, int End) ValidateRange(CopySource source, int total, string display)
    {
        if (source.StartLine < 1 || source.StartLine > total)
        {
            throw new PatchValidationException($"from.startLine {source.StartLine} is out of range ({display} has {total} lines).");
        }

        if (source.EndLine < source.StartLine)
        {
            throw new PatchValidationException($"from.endLine {source.EndLine} must be at least startLine {source.StartLine}.");
        }

        if (source.EndLine > total)
        {
            throw new PatchValidationException($"from.endLine {source.EndLine} is out of range ({display} has {total} lines).");
        }

        return (source.StartLine, source.EndLine);
    }

    private static void CheckExpectations(CopySource source, List<string> lines, int start, int end, string display)
    {
        if (source.Remove && (string.IsNullOrWhiteSpace(source.ExpectFirstLine) || string.IsNullOrWhiteSpace(source.ExpectLastLine)))
        {
            throw new PatchValidationException("from.remove deletes lines from the source, so expectFirstLine and expectLastLine are required.");
        }

        CheckLine(source.ExpectFirstLine, lines, start, "first", display);
        CheckLine(source.ExpectLastLine, lines, end, "last", display);
    }

    private static void CheckLine(string? expected, List<string> lines, int lineNumber, string which, string display)
    {
        if (expected is null)
        {
            return;
        }

        if (lineNumber < 1 || lineNumber > lines.Count)
        {
            throw new PatchValidationException($"The {which} line was checked but {display} has no line {lineNumber}.");
        }

        var actual = lines[lineNumber - 1].Trim();
        if (!string.Equals(actual, expected.Trim(), StringComparison.Ordinal))
        {
            throw new PatchValidationException(
                $"The {which} line of the range ({display} line {lineNumber}) is '{Shorten(actual)}', not '{Shorten(expected.Trim())}'. The file changed or the line numbers are wrong; read it again. Nothing was copied.");
        }
    }

    private string Transform(string text, CopySource source, List<string> notes)
    {
        var current = text;
        if (source.Replace is { Count: > 0 } rules)
        {
            current = TextTransforms.ApplyReplacements(current, rules, options.MaxTransformsPerCopy, notes);
        }

        if (source.Csharp is not null)
        {
            current = ApplyCSharp(current, source.Csharp, notes);
        }

        return TextTransforms.ShiftIndent(current, source.Indent);
    }

    private string ApplyCSharp(string text, CSharpRenames renames, List<string> notes)
    {
        var map = new CSharpRewriteMap(
            renames.Namespaces ?? new Dictionary<string, string>(),
            renames.Identifiers ?? new Dictionary<string, string>());
        if (map.IsEmpty)
        {
            throw new PatchValidationException("csharp needs namespaces and/or identifiers to rename.");
        }

        if (rewriter is null)
        {
            throw new PatchValidationException("C# renames need the code module, which is not available.");
        }

        try
        {
            var result = rewriter.Rewrite(text, map);
            var note = string.Create(CultureInfo.InvariantCulture, $"{result.NamespaceHits} namespace and {result.IdentifierHits} identifier rename(s)");
            if (result.SyntaxErrors > 0)
            {
                note += string.Create(CultureInfo.InvariantCulture, $" ({result.SyntaxErrors} syntax error(s) already in the text)");
            }

            notes.Add(note);
            return result.Text;
        }
        catch (ArgumentException ex)
        {
            throw new PatchValidationException($"csharp: {ex.Message}");
        }
    }

    private async Task RejectSecretsAsync(string text, string display, CancellationToken ct)
    {
        var findings = await inspector.InspectAsync(text, ct).ConfigureAwait(false);
        if (findings.Count > 0)
        {
            throw new PatchValidationException(
                $"The text copied from {display} contains {SensitiveDataScanner.Summarize(findings)}. Waymark does not copy secrets. Copy a range without them. Nothing was copied.");
        }
    }

    private static string LinesToText(List<string> lines) => string.Join('\n', lines) + "\n";

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var breaks = text.Count(c => c == '\n');
        return text[^1] == '\n' ? breaks : breaks + 1;
    }

    private static string Shorten(string value) => value.Length <= ShownCharacters ? value : value[..ShownCharacters] + "...";
}
