using Waymark.Core.Platform;

namespace Waymark.Tests.Platform;

public sealed class PathComparisonsTests
{
    [Fact]
    public void CaseIsIgnoredOnWindowsAndMacOnly()
    {
        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

        Assert.Equal(expected, PathComparisons.IgnoreCase);
        Assert.Equal(expected, string.Equals("Src/A.cs", "src/a.cs", PathComparisons.Comparison));
        Assert.Equal(expected, PathComparisons.Comparer.Equals("Src/A.cs", "src/a.cs"));
    }
}
