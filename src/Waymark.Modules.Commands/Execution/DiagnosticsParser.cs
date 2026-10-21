using System.Globalization;
using System.Text.RegularExpressions;

namespace Waymark.Modules.Commands.Execution;

public sealed record BuildDiagnostic(bool IsError, string Code, string? File, int Line, string Message);

public sealed record TestSummary(int Passed, int Failed, int Skipped, int Total, IReadOnlyList<string> FailedTests);

/// <summary>Reads compiler and test output for the report that Claude receives. All patterns have a time limit so odd output cannot hang a run.</summary>
public static partial class DiagnosticsParser
{
    private const int MaxDiagnostics = 200;

    [GeneratedRegex(@"^(?<file>.+?)\((?<line>\d+)(?:,\d+)?(?:,\d+,\d+)?\):\s+(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+):\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\]]+)\])?\s*$", RegexOptions.None, 200)]
    private static partial Regex LocatedPattern();

    [GeneratedRegex(@"^(?:.+?:\s+)?(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+):\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\]]+)\])?\s*$", RegexOptions.None, 200)]
    private static partial Regex UnlocatedPattern();

    [GeneratedRegex(@"(?:Failed|Passed)!\s+-\s+Failed:\s+(?<failed>\d+),\s+Passed:\s+(?<passed>\d+),\s+Skipped:\s+(?<skipped>\d+),\s+Total:\s+(?<total>\d+)", RegexOptions.None, 200)]
    private static partial Regex TestTotalsPattern();

    [GeneratedRegex(@"^\s+Failed\s+(?<name>\S.*?)(?:\s+\[[^\]]*\])?\s*$", RegexOptions.None, 200)]
    private static partial Regex FailedTestPattern();

    /// <summary>Errors first, repeated lines (the same message is printed once per target framework and again in the summary) removed, paths made relative to the workspace.</summary>
    public static IReadOnlyList<BuildDiagnostic> ParseBuild(string plainOutput, string workspaceRoot)
    {
        var found = new List<BuildDiagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in plainOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line.Length > 2000)
            {
                continue;
            }

            var diagnostic = TryParse(line, workspaceRoot);
            if (diagnostic is null)
            {
                continue;
            }

            var key = string.Create(CultureInfo.InvariantCulture, $"{diagnostic.IsError}|{diagnostic.Code}|{diagnostic.File}|{diagnostic.Line}|{diagnostic.Message}");
            if (seen.Add(key))
            {
                found.Add(diagnostic);
            }
        }

        return found.OrderByDescending(d => d.IsError).ThenBy(d => d.File, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Line).Take(MaxDiagnostics).ToList();
    }

    public static TestSummary? ParseTests(string plainOutput)
    {
        try
        {
            var totals = TestTotalsPattern().Matches(plainOutput);
            if (totals.Count == 0)
            {
                return null;
            }

            var passed = 0;
            var failed = 0;
            var skipped = 0;
            var total = 0;
            foreach (Match match in totals)
            {
                passed += Number(match, "passed");
                failed += Number(match, "failed");
                skipped += Number(match, "skipped");
                total += Number(match, "total");
            }

            var names = new List<string>();
            foreach (var rawLine in plainOutput.Split('\n'))
            {
                var match = FailedTestPattern().Match(rawLine.TrimEnd('\r'));
                if (match.Success && !names.Contains(match.Groups["name"].Value, StringComparer.Ordinal))
                {
                    names.Add(match.Groups["name"].Value);
                }
            }

            return new TestSummary(passed, failed, skipped, total, names.Take(50).ToList());
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static BuildDiagnostic? TryParse(string line, string workspaceRoot)
    {
        try
        {
            var located = LocatedPattern().Match(line);
            if (located.Success)
            {
                return new BuildDiagnostic(
                    IsError(located),
                    located.Groups["code"].Value,
                    Relative(located.Groups["file"].Value, workspaceRoot),
                    Number(located, "line"),
                    located.Groups["msg"].Value.Trim());
            }

            var unlocated = UnlocatedPattern().Match(line);
            if (unlocated.Success)
            {
                return new BuildDiagnostic(IsError(unlocated), unlocated.Groups["code"].Value, null, 0, unlocated.Groups["msg"].Value.Trim());
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        return null;
    }

    private static bool IsError(Match match) => string.Equals(match.Groups["sev"].Value, "error", StringComparison.Ordinal);

    private static int Number(Match match, string group) =>
        int.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static string Relative(string path, string workspaceRoot)
    {
        var normalized = path.Trim();
        if (!Path.IsPathRooted(normalized))
        {
            return normalized;
        }

        var relative = Path.GetRelativePath(workspaceRoot, normalized);
        return relative.StartsWith("..", StringComparison.Ordinal) ? normalized : relative.Replace('\\', '/');
    }
}
