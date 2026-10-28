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

    // ruff --output-format concise:  src/app.py:12:5: F401 [*] `os` imported but unused
    [GeneratedRegex(@"^(?<file>.+?):(?<line>\d+):(?<col>\d+): (?<code>[A-Z]{1,5}\d{1,4})\s+(?:\[\*\]\s+)?(?<msg>.+)$", RegexOptions.None, 200)]
    private static partial Regex RuffPattern();

    // mypy:  src/app.py:12: error: Incompatible types  [assignment]   (with --show-column-numbers: src/app.py:12:5: error: ...)
    [GeneratedRegex(@"^(?<file>.+?):(?<line>\d+):(?:(?<col>\d+):)? (?<sev>error|warning): (?<msg>.+?)(?:\s+\[(?<code>[A-Za-z0-9-]+)\])?$", RegexOptions.None, 200)]
    private static partial Regex MypyPattern();

    // pyright:    /abs/src/app.py:12:5 - error: Type "int" is not assignable (reportAssignmentType)
    [GeneratedRegex(@"^\s*(?<file>.+?):(?<line>\d+):(?<col>\d+) - (?<sev>error|warning): (?<msg>.+?)(?:\s+\((?<code>report[A-Za-z]+)\))?$", RegexOptions.None, 200)]
    private static partial Regex PyrightPattern();

    // pytest -q:  2 failed, 10 passed, 1 skipped in 3.21s   (-v and the default style wrap it in '=' signs)
    [GeneratedRegex(@"^[= ]*(?<body>\d+ [a-z]+(?:, \d+ [a-z]+)*) in [\d.]+s(?: \([\d:]+\))?[= ]*$", RegexOptions.None, 200)]
    private static partial Regex PytestSummaryPattern();

    [GeneratedRegex(@"^[= ]*no tests ran in [\d.]+s", RegexOptions.None, 200)]
    private static partial Regex PytestNoTestsPattern();

    [GeneratedRegex(@"(?<count>\d+) (?<kind>passed|failed|skipped|errors?)", RegexOptions.None, 200)]
    private static partial Regex PytestCountPattern();

    [GeneratedRegex(@"^(?:FAILED|ERROR) (?<name>\S+)", RegexOptions.None, 200)]
    private static partial Regex PytestFailedPattern();

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

    /// <summary>
    /// The findings of ruff (concise format), mypy and pyright, errors first. All three print one finding per line with a path, a line and a message;
    /// a mypy finding without a code is reported as 'mypy'. Notes and hints are left out.
    /// </summary>
    public static IReadOnlyList<BuildDiagnostic> ParsePython(string plainOutput, string workspaceRoot)
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

            var diagnostic = TryParsePython(line, workspaceRoot);
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

    /// <summary>The totals and the failed tests of a pytest run, read from its summary line (<c>2 failed, 10 passed in 1.2s</c>) and the <c>FAILED</c> lines of -ra.</summary>
    public static TestSummary? ParsePytest(string plainOutput)
    {
        try
        {
            string? summary = null;
            var failedNames = new List<string>();
            foreach (var rawLine in plainOutput.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                var failed = PytestFailedPattern().Match(line);
                if (failed.Success && !failedNames.Contains(failed.Groups["name"].Value, StringComparer.Ordinal))
                {
                    failedNames.Add(failed.Groups["name"].Value);
                    continue;
                }

                var totals = PytestSummaryPattern().Match(line);
                if (totals.Success)
                {
                    summary = totals.Groups["body"].Value;
                }
                else if (PytestNoTestsPattern().IsMatch(line))
                {
                    summary = string.Empty;
                }
            }

            if (summary is null)
            {
                return null;
            }

            var passed = 0;
            var failedCount = 0;
            var skipped = 0;
            foreach (Match part in PytestCountPattern().Matches(summary))
            {
                var count = int.Parse(part.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
                switch (part.Groups["kind"].Value)
                {
                    case "passed":
                        passed += count;
                        break;
                    case "failed":
                    case "error":
                    case "errors":
                        failedCount += count;
                        break;
                    case "skipped":
                        skipped += count;
                        break;
                }
            }

            return new TestSummary(passed, failedCount, skipped, passed + failedCount + skipped, failedNames.Take(50).ToList());
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static BuildDiagnostic? TryParsePython(string line, string workspaceRoot)
    {
        try
        {
            var ruff = RuffPattern().Match(line);
            if (ruff.Success)
            {
                return new BuildDiagnostic(true, ruff.Groups["code"].Value, Relative(ruff.Groups["file"].Value, workspaceRoot), Number(ruff, "line"), ruff.Groups["msg"].Value.Trim());
            }

            var mypy = MypyPattern().Match(line);
            if (mypy.Success)
            {
                var code = mypy.Groups["code"].Success ? mypy.Groups["code"].Value : "mypy";
                return new BuildDiagnostic(IsError(mypy), code, Relative(mypy.Groups["file"].Value, workspaceRoot), Number(mypy, "line"), mypy.Groups["msg"].Value.Trim());
            }

            var pyright = PyrightPattern().Match(line);
            if (pyright.Success)
            {
                var code = pyright.Groups["code"].Success ? pyright.Groups["code"].Value : "pyright";
                return new BuildDiagnostic(IsError(pyright), code, Relative(pyright.Groups["file"].Value, workspaceRoot), Number(pyright, "line"), pyright.Groups["msg"].Value.Trim());
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        return null;
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
