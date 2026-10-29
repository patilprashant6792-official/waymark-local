using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Waymark.Core.Code;

namespace Waymark.Languages.TypeScript;

/// <summary>
/// Tree-sitter indexer for TypeScript and JavaScript: <c>.ts .tsx .mts .cts .js .jsx .mjs .cjs</c>. Syntax only; the code is never run, bundled or
/// type-checked, and a file that does not parse still gives what could be read (tree-sitter keeps going after an error).
/// Minified and bundled files are left out: they are one enormous line and say nothing a reader can use.
/// </summary>
public sealed class TypeScriptIndexer(CodeIndexLimits limits, ILogger<TypeScriptIndexer> logger) : ILanguageIndexer
{
    private const int MinifiedMinBytes = 20_000;
    private const int MinifiedAverageLineChars = 400;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs",
    };

    private bool _reportedUnavailable;

    /// <summary>JavaScript files are labelled JavaScript, the rest TypeScript; both come from this indexer.</summary>
    public CodeLanguage Language => CodeLanguage.TypeScript;

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

        if (!SourceTextReader.TryDecode(content, out var decoded))
        {
            return new SyntaxIndexResult(SyntaxIndexOutcome.NotText, null, false);
        }

        var text = SourceTextReader.NormalizeLineBreaks(decoded);
        var lineCount = SourceTextReader.CountLines(text);
        if (IsMinified(relativePath, text, lineCount))
        {
            return new SyntaxIndexResult(SyntaxIndexOutcome.NotText, null, false);
        }

        var extension = Path.GetExtension(relativePath);
        TypeScriptExtraction extraction;
        try
        {
            extraction = Parse(text, TypeScriptGrammars.GrammarFor(extension), TypeScriptModuleNames.FromPath(relativePath), lineCount, ct);
        }
        catch (GrammarUnavailableException ex)
        {
            if (!_reportedUnavailable)
            {
                _reportedUnavailable = true;
                logger.LogWarning(ex, "TypeScript and JavaScript files cannot be indexed on this computer: the tree-sitter library did not load. Other languages are not affected.");
            }

            return new SyntaxIndexResult(SyntaxIndexOutcome.Unavailable, null, false);
        }

        var stamp = new CodeFileStamp(relativePath, hash, content.Length, new DateTimeOffset(modifiedUtc, TimeSpan.Zero));
        var isJavaScript = extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cjs", StringComparison.OrdinalIgnoreCase);
        var file = new CodeFileIndex(
            stamp,
            lineCount,
            TypeScriptFileClassifier.Classify(relativePath, extraction.Types, extraction.Members, extraction.CallSites),
            extraction.Imports,
            extraction.Types,
            extraction.Members,
            extraction.CallSites,
            isJavaScript ? CodeLanguage.JavaScript : CodeLanguage.TypeScript);
        return new SyntaxIndexResult(SyntaxIndexOutcome.Indexed, file, extraction.Truncated);
    }

    private TypeScriptExtraction Parse(string text, string grammar, string moduleName, int lineCount, CancellationToken ct)
    {
        using var parser = TypeScriptGrammars.CreateParser(grammar);
        using var tree = parser.Parse(text) ?? throw new InvalidDataException("The parser returned no tree.");
        return new TypeScriptExtractor(moduleName, lineCount, limits, ct).Extract(tree.RootNode);
    }

    /// <summary>A .min.js file, or a big file whose lines are very long on average (a bundle).</summary>
    private static bool IsMinified(string relativePath, string text, int lineCount)
    {
        var name = Path.GetFileName(relativePath);
        if (name.Contains(".min.", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bundle.js", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return text.Length >= MinifiedMinBytes && lineCount > 0 && text.Length / lineCount > MinifiedAverageLineChars;
    }
}
