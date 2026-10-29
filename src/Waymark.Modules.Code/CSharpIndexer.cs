using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Waymark.Core.Code;
using Waymark.Modules.Code.Indexing;

namespace Waymark.Modules.Code;

/// <summary>Roslyn syntax-only indexer: no compilation, no references, so it is fast and never loads the user's code.</summary>
public sealed class CSharpIndexer(CodeIndexOptions options) : ILanguageIndexer
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs" };

    public CodeLanguage Language => CodeLanguage.CSharp;

    public IReadOnlySet<string> Extensions => SupportedExtensions;

    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Latest, DocumentationMode.Parse, SourceCodeKind.Regular);

    public Task<SyntaxIndexResult> IndexFileAsync(string absolutePath, string relativePath, string? knownHash, CancellationToken ct) =>
        IndexFileAsync(absolutePath, relativePath, knownHash, null, ct);

    public async Task<SyntaxIndexResult> IndexFileAsync(string absolutePath, string relativePath, string? knownHash, DateTimeOffset? modifiedUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var modified = modifiedUtc ?? new DateTimeOffset(File.GetLastWriteTimeUtc(absolutePath), TimeSpan.Zero);
        byte[] content;
        await using (var stream = new FileStream(
            absolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length > options.MaxFileBytes)
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

        SourceText text;
        try
        {
            text = SourceText.From(content, content.Length, encoding: null, checksumAlgorithm: SourceHashAlgorithm.Sha256, throwIfBinaryDetected: true);
        }
        catch (InvalidDataException)
        {
            return new SyntaxIndexResult(SyntaxIndexOutcome.NotText, null, false);
        }

        var tree = CSharpSyntaxTree.ParseText(text, ParseOptions, relativePath, ct);
        var extraction = new SyntaxExtractor(text, options, ct).Extract(tree.GetCompilationUnitRoot(ct));
        var stamp = new CodeFileStamp(relativePath, hash, content.Length, modified);
        var file = new CodeFileIndex(
            stamp,
            SourceLines.Count(text),
            FileClassifier.Classify(extraction.HasTopLevelStatements, extraction.Types, extraction.Members, extraction.CallSites),
            extraction.Usings,
            extraction.Types,
            extraction.Members,
            extraction.CallSites);
        return new SyntaxIndexResult(SyntaxIndexOutcome.Indexed, file, extraction.Truncated);
    }
}
