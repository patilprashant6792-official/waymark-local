using System.Globalization;
using Waymark.Modules.Commands.Execution;
using Waymark.Modules.Commands.Terminals;
using Waymark.Tests.Support;

namespace Waymark.Tests.Commands;

public sealed class TerminalLogTests
{
    private const int Files = 3;
    private const long MaxBytes = 64 * 1024;

    // Each line names its own number, so a line read anywhere can be checked against the number it was given.
    private static string LineText(int number) => string.Create(CultureInfo.InvariantCulture, $"L{number} ") + new string('x', 100);

    private static TerminalLog Filled(TempDirectory folder, int lines, out string path)
    {
        path = folder.PathOf("run-1.log");
        var log = new TerminalLog(path, MaxBytes, Files, ringLines: 100);
        for (var i = 1; i <= lines; i++)
        {
            log.Append(LineText(i) + "\n");
        }

        return log;
    }

    private static void AssertNumbered(LogRead read)
    {
        Assert.All(read.Lines, line => Assert.Equal(LineText((int)line.Number), line.Text));
    }

    [Fact]
    public void ReadShowsTheNewestLinesAndCountsTheOnesLeftOut()
    {
        using var folder = new TempDirectory();
        using var log = Filled(folder, 5000, out _);

        var read = log.Read(0, 10);

        Assert.Equal(5000, read.Total);
        Assert.Equal(Enumerable.Range(4991, 10).Select(n => (long)n), read.Lines.Select(l => l.Number));
        Assert.Equal(4990, read.Skipped);
        AssertNumbered(read);
    }

    [Fact]
    public void APageGivesTheNextLinesInOrderAndHowManyComeAfter()
    {
        using var folder = new TempDirectory();
        using var log = Filled(folder, 5000, out _);

        var page = log.ReadPage(4950, 20);

        Assert.Equal(Enumerable.Range(4951, 20).Select(n => (long)n), page.Lines.Select(l => l.Number));
        Assert.Equal(30, page.After);
        AssertNumbered(page);
    }

    [Fact]
    public void LinesOlderThanMemoryComeFromTheFilesWithTheSameNumbers()
    {
        using var folder = new TempDirectory();
        using var log = Filled(folder, 5000, out _);
        var oldest = log.Read(0, 1).OldestKept;
        Assert.True(oldest > 1, "the log should have rotated old files away");
        Assert.True(oldest < 4900, "the kept files should reach further back than memory");

        var page = log.ReadPage(oldest + 9, 50);

        Assert.Equal(oldest + 10, page.Lines[0].Number);
        Assert.Equal(50, page.Lines.Count);
        Assert.True(page.After > 0);
        AssertNumbered(page);
    }

    [Fact]
    public void APageFromBeforeTheKeptFilesStartsAtTheOldestKeptLine()
    {
        using var folder = new TempDirectory();
        using var log = Filled(folder, 5000, out _);
        var oldest = log.Read(0, 1).OldestKept;

        var page = log.ReadPage(0, 5);

        Assert.Equal(oldest, page.Lines[0].Number);
        Assert.Equal(oldest, page.OldestKept);
        AssertNumbered(page);
    }

    [Fact]
    public void TheStoredLogHasTheSameNumbersAfterTheProgramEnded()
    {
        using var folder = new TempDirectory();
        string path;
        long oldest;
        using (var log = Filled(folder, 5000, out path))
        {
            oldest = log.Read(0, 1).OldestKept;
        }

        var tail = TerminalLog.TailStored(path, Files, 10);
        var page = TerminalLog.ReadStored(path, Files, oldest + 99, 25);

        Assert.Equal(5000, tail.Total);
        Assert.Equal(5000, tail.Lines[^1].Number);
        Assert.Equal(oldest, tail.OldestKept);
        AssertNumbered(tail);
        Assert.Equal(oldest + 100, page.Lines[0].Number);
        AssertNumbered(page);
    }

    [Fact]
    public void AVeryLongLineIsSplitTheSameWayInMemoryAndOnDisk()
    {
        using var folder = new TempDirectory();
        var path = folder.PathOf("run-2.log");
        long inMemory;
        using (var log = new TerminalLog(path, MaxBytes, Files))
        {
            log.Append("first\n");
            log.Append(new string('a', 9_000));
            log.Append("\nlast\n");
            inMemory = log.LineCount;
            Assert.Equal("last", log.ReadPage(inMemory - 1, 1).Lines[0].Text);
        }

        var stored = TerminalLog.TailStored(path, Files, 1);

        Assert.Equal(inMemory, stored.Total);
        Assert.Equal("last", stored.Lines[0].Text);
        Assert.Equal(inMemory, stored.Lines[0].Number);
    }

    [Fact]
    public void AnUnfinishedLastLineIsStoredAsALineWhenTheLogCloses()
    {
        using var folder = new TempDirectory();
        var path = folder.PathOf("run-3.log");
        using (var log = new TerminalLog(path, MaxBytes, Files))
        {
            log.Append("one\ntwo\nName? ");
        }

        var stored = TerminalLog.ReadStored(path, Files, 0, 10);

        Assert.Equal(new[] { "one", "two", "Name?" }, stored.Lines.Select(l => l.Text.TrimEnd()));
        Assert.Equal(3, stored.Total);
    }

    [Fact]
    public void ASearchGivesTheFirstHitsInOrderAndCountsTheRest()
    {
        using var folder = new TempDirectory();
        using var log = Filled(folder, 300, out _);

        // L1, L10-L19, L100-L199: 111 lines start with "L1"; memory keeps the last 100 lines, so the search reads the file too.
        var hits = log.GrepPage("L1", 0, 5);

        Assert.Equal(new long[] { 1, 10, 11, 12, 13 }, hits.Lines.Select(l => l.Number));
        Assert.Equal(111 - 5, hits.After);
        AssertNumbered(hits);
    }

    [Fact]
    public void CompanionPathsIncludeTheRotatedFilesAndTheIndex()
    {
        var paths = TerminalLog.CompanionPaths("/runs/run-7.log", 3);

        Assert.Contains(paths, p => p.EndsWith("run-7.1.log", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("run-7.2.log", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("run-7.log.lines", StringComparison.Ordinal));
    }

    [Fact]
    public void WaymarksOwnLinesDoNotMakeATerminalReady()
    {
        using var folder = new TempDirectory();
        var launch = new TerminalLaunch(1, "web", IsShell: true, ["SLOW READY"], [], [], null, TimeSpan.FromSeconds(30), null, null, null, ReadyOnAnyPort: false);
        var runtime = new TerminalRuntime(launch, new TerminalLog(folder.PathOf("run-4.log"), MaxBytes, Files));

        runtime.OnText("$ # zsh, keeps running in terminal web\necho 'SLOW READY'\n# output\n", fromProgram: false);

        Assert.False(runtime.Detector.IsReady);
        Assert.Null(runtime.FirstOutputUtc);
        Assert.Equal(0, runtime.Health.ErrorsAfter(0));

        runtime.OnText("SLOW READY\n", fromProgram: true);

        Assert.True(runtime.Detector.IsReady);
        Assert.NotNull(runtime.FirstOutputUtc);
        Assert.Equal(4, runtime.Log.LineCount);
        runtime.Log.Dispose();
    }
}
