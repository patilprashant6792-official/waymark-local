using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Waymark.Core.Code;

namespace Waymark.Languages.Python;

/// <summary>
/// Tree-sitter indexer for <c>.py</c> and <c>.pyi</c> files: syntax only, the code is never run or imported, and a file that does not parse still gives
/// what could be read (tree-sitter keeps going after an error).
/// </summary>
public sealed class PythonIndexer(CodeIndexLimits limits, ILogger<PythonIndexer> logger) : ILanguageIndexer
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".py", ".pyi" };

    private static readonly char[] LineBreaks = ['\r', '\u0085', '\u2028', '\u2029'];

    private bool _reportedUnavailable;

    public CodeLanguage Language => CodeLanguage.Python;

    public IReadOnlySet<string> Extensions => SupportedExtensions;

    public async Task<SyntaxIndexResult> IndexFileAsync(string absolutePath, string relativePath, string? knownHash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var modifiedUtc = File.GetLastWriteTimeUtc(absolutePath);
        byte[] content;
        await using (var stream = new FileStream(
            absolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length > limits.MaxFileBytes)
            {
                return new SyntaxIndexResult(SyntaxIndexOutcome.TooLarge, null, false);
            }

            content = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(content, ct).ConfigureAwait(false);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        if (string.Equals(hash, knownHash, StringComparison.OrdinalIgnoreCase))
        {
            return new SyntaxIndexResult(SyntaxIndexOutcome.Unchanged, null, false);
        }

        if (!TryDecode(content, out var decoded))
        {
            return new SyntaxIndexResult(SyntaxIndexOutcome.NotText, null, false);
        }

        // Every line break Roslyn's SourceText knows becomes \n, so the rows tree-sitter counts are the lines the readers show.
        var text = NormalizeLineBreaks(decoded);
        var lineCount = CountLines(text);
        PythonExtraction extraction;
        try
        {
            extraction = Parse(text, PythonModuleNames.FromPath(relativePath), lineCount, ct);
        }
        catch (GrammarUnavailableException ex)
        {
            if (!_reportedUnavailable)
            {
                _reportedUnavailable = true;
                logger.LogWarning(ex, "Python files cannot be indexed on this computer: the tree-sitter library did not load. Other languages are not affected.");
            }

            return new SyntaxIndexResult(SyntaxIndexOutcome.Unavailable, null, false);
        }

        var stamp = new CodeFileStamp(relativePath, hash, content.Length, new DateTimeOffset(modifiedUtc, TimeSpan.Zero));
        var file = new CodeFileIndex(
            stamp,
            lineCount,
            PythonFileClassifier.Classify(relativePath, extraction.Types, extraction.Members, extraction.CallSites),
            extraction.Imports,
            extraction.Types,
            extraction.Members,
            extraction.CallSites,
            CodeLanguage.Python);
        return new SyntaxIndexResult(SyntaxIndexOutcome.Indexed, file, extraction.Truncated);
    }

    private PythonExtraction Parse(string text, string moduleName, int lineCount, CancellationToken ct)
    {
        using var parser = PythonGrammar.CreateParser();
        using var tree = parser.Parse(text) ?? throw new InvalidDataException("The parser returned no tree.");
        return new PythonExtractor(moduleName, lineCount, limits, ct).Extract(tree.RootNode);
    }

    /// <summary>UTF-8 (with or without a byte order mark) or UTF-16 with a mark. A file with NUL bytes and no UTF-16 mark is binary.</summary>
    private static bool TryDecode(byte[] content, out string text)
    {
        text = string.Empty;
        var utf16 = content.Length >= 2 && ((content[0] == 0xFF && content[1] == 0xFE) || (content[0] == 0xFE && content[1] == 0xFF));
        if (!utf16 && Array.IndexOf(content, (byte)0) >= 0)
        {
            return false;
        }

        using var reader = new StreamReader(new MemoryStream(content, writable: false), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        text = reader.ReadToEnd();
        return true;
    }

    private static string NormalizeLineBreaks(string text)
    {
        if (text.IndexOfAny(LineBreaks) < 0)
        {
            return text;
        }

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u0085', '\n')
            .Replace('\u2028', '\n')
            .Replace('\u2029', '\n');
    }

    /// <summary>Lines as an editor shows them: a final line break does not start another line, an empty file has none.</summary>
    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var lines = 1;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        return text[^1] == '\n' ? lines - 1 : lines;
    }
}
