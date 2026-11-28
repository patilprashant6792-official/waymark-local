using Microsoft.Extensions.Logging.Abstractions;
using Waymark.Modules.Commands.Execution;
using Waymark.Tests.Support;

namespace Waymark.Tests.Commands;

public sealed class RunOutputFilesTests
{
    [Fact]
    public void DeletesTheOutputWithItsRotatedFilesAndIndex()
    {
        using var dir = new TempDirectory();
        var runs = Path.Combine(dir.Root, "runs");
        Directory.CreateDirectory(runs);
        var log = Path.Combine(runs, "run-7.log");
        string[] files = [log, Path.Combine(runs, "run-7.1.log"), Path.Combine(runs, "run-7.2.log"), log + ".lines"];
        foreach (var file in files)
        {
            File.WriteAllText(file, "x");
        }

        var deleted = RunOutputFiles.Delete(log, runs, 3, NullLogger.Instance);

        Assert.Equal(4, deleted);
        Assert.All(files, f => Assert.False(File.Exists(f)));
    }

    [Fact]
    public void NeverDeletesAFileOutsideTheRunsFolder()
    {
        using var dir = new TempDirectory();
        var runs = Path.Combine(dir.Root, "runs");
        Directory.CreateDirectory(runs);
        var outside = Path.Combine(dir.Root, "important.txt");
        File.WriteAllText(outside, "keep me");

        var deleted = RunOutputFiles.Delete(outside, runs, 3, NullLogger.Instance);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void APathThatClimbsOutOfTheRunsFolderIsRefused()
    {
        using var dir = new TempDirectory();
        var runs = Path.Combine(dir.Root, "runs");
        Directory.CreateDirectory(runs);
        var outside = Path.Combine(dir.Root, "secret.log");
        File.WriteAllText(outside, "keep me");

        var deleted = RunOutputFiles.Delete(Path.Combine(runs, "..", "secret.log"), runs, 3, NullLogger.Instance);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void MissingFilesAreSkippedQuietly()
    {
        using var dir = new TempDirectory();
        var runs = Path.Combine(dir.Root, "runs");
        Directory.CreateDirectory(runs);

        Assert.Equal(0, RunOutputFiles.Delete(Path.Combine(runs, "run-9.log"), runs, 3, NullLogger.Instance));
    }
}
