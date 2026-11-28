using Waymark.Modules.Commands.Execution;

namespace Waymark.Tests.Commands;

public sealed class ProcessTreeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 28, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WalksTheWholeTreeNearestFirst()
    {
        // npm (10) -> node vite (11) -> esbuild (12); an unrelated process (20) under another parent.
        (int, int)[] table = [(10, 1), (11, 10), (12, 11), (20, 2), (13, 10)];

        var below = ProcessTree.Descendants(10, table, startOf: null);

        Assert.Equal([11, 13, 12], below);
    }

    [Fact]
    public void AProcessIsNeverItsOwnChild()
    {
        (int, int)[] table = [(0, 0), (4, 4), (5, 4)];

        Assert.Equal([5], ProcessTree.Descendants(4, table, startOf: null));
    }

    [Fact]
    public void AChildOlderThanItsParentIsLeftOut()
    {
        // Windows reuses ids: 30 claims parent 10, but it started an hour before 10 did, so its real parent was an earlier process with that id.
        (int, int)[] table = [(11, 10), (30, 10)];
        var starts = new Dictionary<int, DateTimeOffset> { [10] = T0, [11] = T0.AddSeconds(2), [30] = T0.AddHours(-1) };

        var below = ProcessTree.Descendants(10, table, pid => starts.TryGetValue(pid, out var s) ? s : null);

        Assert.Equal([11], below);
    }

    [Fact]
    public void AChildWhoseStartCannotBeReadIsLeftOutWhenStartsAreChecked()
    {
        (int, int)[] table = [(11, 10), (12, 10)];
        var starts = new Dictionary<int, DateTimeOffset> { [10] = T0, [11] = T0.AddSeconds(1) };

        Assert.Equal([11], ProcessTree.Descendants(10, table, pid => starts.TryGetValue(pid, out var s) ? s : null));
    }

    [Fact]
    public void ReadsTheOutputOfPs()
    {
        var output = "  501     1\n  777   501\nnot a line\n\n  778\n";

        Assert.Equal([(501, 1), (777, 501)], ProcessTree.ParsePs(output));
    }
}
