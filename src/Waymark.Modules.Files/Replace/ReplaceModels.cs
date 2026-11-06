using Waymark.Core.Text;

namespace Waymark.Modules.Files.Replace;

/// <summary>How file_replace finds the text: a literal string, or a C# identifier found by syntax.</summary>
public enum ReplaceMode
{
    Literal,
    CSharp,
}

/// <summary>The arguments of one file_replace call, as the agent sent them. Give PathGlob or Paths, not both.</summary>
public sealed record ReplaceRequest(
    string? Workspace,
    string? Find,
    string? With,
    string? PathGlob,
    IReadOnlyList<string>? Paths,
    ReplaceMode Mode = ReplaceMode.Literal,
    bool CaseSensitive = true,
    bool WholeWord = false);

/// <summary>One line of the preview: its number and its text with every match marked.</summary>
public sealed record PreviewLine(int Number, string Text);

/// <summary>A file with matches. Lines holds the first lines only; MoreLines counts the lines with matches not shown.</summary>
public sealed record PreviewFile(string RelativePath, int Matches, int SyntaxErrors, IReadOnlyList<PreviewLine> Lines, int MoreLines);

/// <summary>What one scan read and what it left out, by reason.</summary>
public sealed class ScanSummary
{
    public int Read { get; set; }

    public int TooLarge { get; set; }

    public int VendoredOrMinified { get; set; }

    public int Unreadable { get; set; }

    public int NotText { get; set; }

    public int NotCSharp { get; set; }

    public int Unscanned { get; set; }

    public bool Truncated { get; set; }

    public bool BudgetExceeded { get; set; }
}

/// <summary>
/// What a preview found. PlanId is set only when the scan was complete and within the limits; Blockers then is empty.
/// Without a plan id nothing can be applied, so a partial scan can never be written.
/// </summary>
public sealed record ReplacePreview(
    string? PlanId,
    DateTimeOffset? ExpiresUtc,
    int MatchCount,
    IReadOnlyList<PreviewFile> Files,
    ScanSummary Summary,
    IReadOnlyList<CatalogRefusal> Refused,
    IReadOnlyList<string> Blockers);

/// <summary>A file the apply changed and how many places in it.</summary>
public sealed record AppliedFile(string RelativePath, int Replacements);

/// <summary>Applied is false when the call was refused; Refusal then says why, and nothing was written.</summary>
public sealed record ReplaceApplyResult(bool Applied, string? Refusal, IReadOnlyList<AppliedFile> Files, int Excluded)
{
    public static ReplaceApplyResult Refused(string reason) => new(false, reason, [], 0);
}

/// <summary>A place to replace: the first character and the length, in the decoded text of the file.</summary>
internal readonly record struct TextRange(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>A planned replacement and the 1-based line it is on.</summary>
internal sealed record PlannedSpan(int Start, int Length, int Line);

/// <summary>One file of a plan: its path, the SHA-256 of the bytes the preview read, and every place to change.</summary>
internal sealed record FilePlan(string RelativePath, string Sha256, IReadOnlyList<PlannedSpan> Spans);

/// <summary>
/// What a preview saw, kept on the server so the apply writes exactly that. Signature holds the normalised arguments;
/// an apply with other arguments is refused. Holds no file text.
/// </summary>
internal sealed record ReplacePlan(string Id, long WorkspaceId, string Signature, IReadOnlyList<FilePlan> Files, DateTimeOffset ExpiresUtc)
{
    public int MatchCount => Files.Sum(file => file.Spans.Count);
}

/// <summary>A refusal that leaves every file as it was; the message says why and what to do.</summary>
internal sealed class ReplaceRefusedException(string message) : Exception(message);
