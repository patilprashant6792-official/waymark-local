using System.Text;
using Waymark.Core.Platform;
using Waymark.Tests.Support;

namespace Waymark.Tests.Platform;

public sealed class RealPathTests
{
    [Fact]
    public void WindowsPathsAreLeftAsTheyAre()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var typed = temp.Root.ToUpperInvariant();

        Assert.Equal(typed, RealPath.OfDirectory(typed));
    }

    [Fact]
    public void AnExactPathStaysTheSame()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var real = RealPath.OfDirectory(temp.Root);
        Directory.CreateDirectory(Path.Combine(real, "repo"));

        Assert.Equal(Path.Combine(real, "repo"), RealPath.OfDirectory(Path.Combine(real, "repo")));
    }

    [Fact]
    public void LinksOnTheWayAreResolved()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var real = RealPath.OfDirectory(temp.Root);
        Directory.CreateDirectory(Path.Combine(real, "target", "inner"));
        Directory.CreateSymbolicLink(Path.Combine(real, "alias"), Path.Combine(real, "target"));

        Assert.Equal(Path.Combine(real, "target", "inner"), RealPath.OfDirectory(Path.Combine(real, "alias", "inner")));
    }

    [Fact]
    public void OnACaseInsensitiveVolumeTheStoredSpellingWins()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var real = RealPath.OfDirectory(temp.Root);
        Directory.CreateDirectory(Path.Combine(real, "Repo"));
        var typed = Path.Combine(real, "repo");
        if (!Directory.Exists(typed))
        {
            // A case-sensitive volume (Linux, or a case-sensitive APFS disk): the other spelling is another folder.
            return;
        }

        Assert.Equal(Path.Combine(real, "Repo"), RealPath.OfDirectory(typed));
    }

    [Fact]
    public void ADecomposedNameIsMatchedByItsComposedSpelling()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var real = RealPath.OfDirectory(temp.Root);
        var decomposed = "Zoe\u0308";
        Directory.CreateDirectory(Path.Combine(real, decomposed));
        var typed = Path.Combine(real, decomposed.Normalize(NormalizationForm.FormC));
        if (!Directory.Exists(typed))
        {
            // A file system that keeps both forms apart: nothing to match.
            return;
        }

        var stored = Path.GetFileName(RealPath.OfDirectory(typed));
        Assert.Equal(decomposed.Normalize(NormalizationForm.FormC), stored.Normalize(NormalizationForm.FormC));
        Assert.True(Directory.Exists(Path.Combine(real, stored)));
    }

    [Fact]
    public void AMissingPartKeepsTheRestAsGiven()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var real = RealPath.OfDirectory(temp.Root);

        Assert.Equal(Path.Combine(real, "missing", "deeper"), RealPath.OfDirectory(Path.Combine(real, "missing", "deeper")));
    }
}
