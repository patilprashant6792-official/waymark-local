namespace Waymark.Modules.Code.Content;

/// <summary>A file the walker found that may be searched. RelativePath uses '/'.</summary>
internal sealed record ContentFile(string RelativePath, string AbsolutePath, long Size);

/// <summary>Files to search in one workspace, in path order, plus what was left out on the way.</summary>
internal sealed record ContentWalk(IReadOnlyList<ContentFile> Files, ScanCoverage Coverage, bool Truncated);

internal enum ScanOutcome
{
    Scanned,
    Binary,
    Minified,
    Unreadable,
}

internal sealed record MatchedLine(int Number, string Text);

/// <summary>What reading one file gave. Lines holds at most MaxLinesPerFile entries; MatchingLines counts all of them.</summary>
internal sealed record FileScan(ScanOutcome Outcome, int MatchingLines, IReadOnlyList<MatchedLine> Lines)
{
    public static FileScan Skipped(ScanOutcome outcome) => new(outcome, 0, []);
}

internal sealed record FileHits(string Workspace, string Path, int MatchingLines, IReadOnlyList<MatchedLine> Lines);

/// <summary>How many files were read and how many were left out, by reason. Lets a result say what it did not cover.</summary>
internal sealed class ScanCoverage
{
    public int Scanned { get; set; }

    public int TooLarge { get; set; }

    public int VendoredOrMinified { get; set; }

    public int Binary { get; set; }

    public int Unreadable { get; set; }

    public void Add(ScanCoverage other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Scanned += other.Scanned;
        TooLarge += other.TooLarge;
        VendoredOrMinified += other.VendoredOrMinified;
        Binary += other.Binary;
        Unreadable += other.Unreadable;
    }
}

/// <summary>Everything one code_grep call found and everything that limited it; the renderer turns it into text.</summary>
internal sealed class TextSearchReport(string query, bool caseSensitive, IReadOnlyList<string> workspaces)
{
    public string Query { get; } = query;

    public bool CaseSensitive { get; } = caseSensitive;

    public IReadOnlyList<string> Workspaces { get; } = workspaces;

    public List<FileHits> Matches { get; } = [];

    public ScanCoverage Coverage { get; } = new();

    public bool BudgetExceeded { get; set; }

    public int UnscannedFiles { get; set; }

    public bool MatchLimitReached { get; set; }

    public List<string> TruncatedWorkspaces { get; } = [];

    public List<string> UnreachableWorkspaces { get; } = [];
}
