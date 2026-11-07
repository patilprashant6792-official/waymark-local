using System.Text;
using Waymark.Core.Code;
using Waymark.Core.Text;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Files.Replace;

/// <summary>A file the scan found matches in: what the plan keeps (hash, places) and what the preview shows (marked lines).</summary>
internal sealed record ScannedFile(string RelativePath, string Sha256, IReadOnlyList<PlannedSpan> Spans, int SyntaxErrors, IReadOnlyList<PreviewLine> Lines, int MoreLines);

/// <summary>Everything one scan found, and what limited it.</summary>
internal sealed record ScanResult(IReadOnlyList<ScannedFile> Files, ScanSummary Summary, IReadOnlyList<CatalogRefusal> Refused);

/// <summary>
/// The read-only half of file_replace: lists the files through <see cref="ITextFileCatalog"/>, reads each one, finds the places and
/// hashes the bytes. Nothing is written here.
/// </summary>
internal sealed class ReplacePlanner(ITextFileCatalog catalog, ReplaceOptions options, ICSharpRewriter? rewriter = null)
{
    private const int Parallelism = 4;
    private const int MaxExcerptChars = 200;
    private const string CSharpExtension = ".cs";

    private enum Outcome
    {
        Matched,
        NoMatch,
        TooLarge,
        NotText,
        Minified,
        Unreadable,
    }

    private sealed record FileScan(Outcome Outcome, ScannedFile? File = null);

    /// <summary>In C# mode, find and with must be identifiers and not keywords.</summary>
    /// <exception cref="ArgumentException">A name is not a valid C# identifier, or the code module is not loaded.</exception>
    public void ValidateNames(ValidatedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mode != ReplaceMode.CSharp)
        {
            return;
        }

        var csharp = rewriter ?? throw new ArgumentException("mode 'csharp' needs the code module, which is not loaded.");
        csharp.FindIdentifier(string.Empty, request.Find);
        csharp.FindIdentifier(string.Empty, request.With);
    }

    /// <exception cref="InvalidOperationException">No catalog is available, or the workspace folder is missing.</exception>
    public async Task<ScanResult> ScanAsync(Workspace workspace, ValidatedRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        if (!catalog.IsAvailable)
        {
            throw new InvalidOperationException("file_replace lists files through the code module, which is not loaded.");
        }

        var listing = request.Glob is not null
            ? await catalog.ListAsync(workspace, request.Glob, ct).ConfigureAwait(false)
            : await catalog.ListPathsAsync(workspace, request.Paths, ct).ConfigureAwait(false);

        var summary = new ScanSummary
        {
            TooLarge = listing.TooLarge,
            VendoredOrMinified = listing.VendoredOrMinified,
            Unreadable = listing.Unreadable,
            Truncated = listing.Truncated,
        };

        var candidates = listing.Files;
        if (request.Mode == ReplaceMode.CSharp)
        {
            candidates = [.. listing.Files.Where(file => file.RelativePath.EndsWith(CSharpExtension, StringComparison.OrdinalIgnoreCase))];
            summary.NotCSharp = listing.Files.Count - candidates.Count;
        }

        var results = new FileScan?[candidates.Count];
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(options.ScanBudgetSeconds));
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Parallelism, Environment.ProcessorCount), CancellationToken = deadline.Token };
            try
            {
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, candidates.Count),
                    parallel,
                    async (index, token) => results[index] = await ScanFileAsync(candidates[index], request, token).ConfigureAwait(false))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                summary.BudgetExceeded = true;
            }
        }

        var files = new List<ScannedFile>();
        foreach (var result in results)
        {
            if (result is null)
            {
                summary.Unscanned++;
                continue;
            }

            Count(summary, result.Outcome);
            if (result.File is { } file)
            {
                files.Add(file);
            }
        }

        if (summary.Unscanned > 0)
        {
            summary.BudgetExceeded = true;
        }

        return new ScanResult(files, summary, listing.Refused);
    }

    private static void Count(ScanSummary summary, Outcome outcome)
    {
        switch (outcome)
        {
            case Outcome.Matched:
            case Outcome.NoMatch:
                summary.Read++;
                break;
            case Outcome.TooLarge:
                summary.TooLarge++;
                break;
            case Outcome.NotText:
                summary.NotText++;
                break;
            case Outcome.Minified:
                summary.VendoredOrMinified++;
                break;
            default:
                summary.Unreadable++;
                break;
        }
    }

    private async Task<FileScan> ScanFileAsync(CatalogFile file, ValidatedRequest request, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = await FileBytes.ReadAsync(file.AbsolutePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileScan(Outcome.Unreadable);
        }

        if (bytes.Length > catalog.MaxFileBytes)
        {
            return new FileScan(Outcome.TooLarge);
        }

        string text;
        try
        {
            text = TextFileFormat.Detect(bytes).DecodeStrict(bytes);
        }
        catch (Exception ex) when (ex is NotSupportedException or DecoderFallbackException)
        {
            return new FileScan(Outcome.NotText);
        }

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            return new FileScan(Outcome.NotText);
        }

        var map = new LineMap(text);
        if (catalog.LooksMinified(bytes.Length, map.LineCount))
        {
            return new FileScan(Outcome.Minified);
        }

        var (ranges, syntaxErrors) = FindRanges(text, request);
        if (ranges.Count == 0)
        {
            return new FileScan(Outcome.NoMatch);
        }

        return new FileScan(Outcome.Matched, Describe(file.RelativePath, FileBytes.Hash(bytes), map, ranges, syntaxErrors));
    }

    private (IReadOnlyList<TextRange> Ranges, int SyntaxErrors) FindRanges(string text, ValidatedRequest request)
    {
        if (request.Mode == ReplaceMode.Literal)
        {
            return (LiteralMatcher.Find(text, request.Find, request.CaseSensitive, request.WholeWord), 0);
        }

        var search = (rewriter ?? throw new InvalidOperationException("mode 'csharp' needs the code module.")).FindIdentifier(text, request.Find);
        var ranges = search.Spans
            .Select(span => new TextRange(span.Start, span.Length))
            .OrderBy(range => range.Start)
            .ToList();
        return (ranges, search.SyntaxErrors);
    }

    private ScannedFile Describe(string relativePath, string sha256, LineMap map, IReadOnlyList<TextRange> ranges, int syntaxErrors)
    {
        var spans = new List<PlannedSpan>(ranges.Count);
        foreach (var range in ranges)
        {
            spans.Add(new PlannedSpan(range.Start, range.Length, map.LineOf(range.Start)));
        }

        var lines = new List<PreviewLine>();
        var moreLines = 0;
        foreach (var group in spans.GroupBy(span => span.Line))
        {
            if (lines.Count >= options.MaxPreviewLinesPerFile)
            {
                moreLines++;
                continue;
            }

            var start = map.StartOf(group.Key);
            var local = group.Select(span => new TextRange(span.Start - start, span.Length)).ToList();
            lines.Add(new PreviewLine(group.Key, LineMap.Excerpt(map.LineText(group.Key), local, MaxExcerptChars)));
        }

        return new ScannedFile(relativePath, sha256, spans, syntaxErrors, lines, moreLines);
    }
}
