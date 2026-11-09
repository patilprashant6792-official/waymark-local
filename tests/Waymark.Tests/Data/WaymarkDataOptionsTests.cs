using Waymark.Data.Sqlite;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class WaymarkDataOptionsTests
{
    [Fact]
    public void ForDirectoryPutsTheDatabaseFileInThatFolder()
    {
        using var directory = new TempDirectory();

        var options = WaymarkDataOptions.ForDirectory(directory.Root);

        Assert.Equal(Path.Combine(directory.Root, "waymark.db"), options.DatabasePath);
        Assert.Equal(WaymarkDataOptions.DatabaseFileName, Path.GetFileName(options.DatabasePath));
    }

    [Fact]
    public void TheDefaultBusyTimeoutIsFiveSeconds()
    {
        using var directory = new TempDirectory();

        Assert.Equal(TimeSpan.FromSeconds(5), WaymarkDataOptions.ForDirectory(directory.Root).BusyTimeout);
    }

    [Fact]
    public void ARelativePathIsRefused() =>
        Assert.Throws<ArgumentException>(() => new WaymarkDataOptions("relative.db"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankPathIsRefused(string path) =>
        Assert.Throws<ArgumentException>(() => new WaymarkDataOptions(path));

    [Fact]
    public void ANullPathIsRefused() =>
        Assert.ThrowsAny<ArgumentException>(() => new WaymarkDataOptions(null!));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(61)]
    public void ABusyTimeoutOutsideZeroToOneMinuteIsRefused(int seconds)
    {
        using var directory = new TempDirectory();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WaymarkDataOptions(directory.PathOf("a.db"), TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    public void TheBoundsOfTheBusyTimeoutAreAllowed(int seconds)
    {
        using var directory = new TempDirectory();

        var options = new WaymarkDataOptions(directory.PathOf("a.db"), TimeSpan.FromSeconds(seconds));

        Assert.Equal(TimeSpan.FromSeconds(seconds), options.BusyTimeout);
    }

    [Fact]
    public void TheConfiguredDataDirectoryWins()
    {
        using var directory = new TempDirectory();
        var configuration = new TestConfiguration((WaymarkDataOptions.DataDirectoryConfigKey, directory.Root));

        var options = WaymarkDataOptions.FromConfiguration(configuration);

        Assert.Equal(Path.Combine(directory.Root, "waymark.db"), options.DatabasePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void WithoutAConfiguredDirectoryTheDefaultIsUsed(string? configured)
    {
        var configuration = new TestConfiguration((WaymarkDataOptions.DataDirectoryConfigKey, configured));

        var options = WaymarkDataOptions.FromConfiguration(configuration);

        Assert.Equal(
            Path.Combine(Path.GetFullPath(WaymarkDataOptions.DefaultDataDirectory), "waymark.db"),
            options.DatabasePath);
    }

    [Fact]
    public void ANullConfigurationIsRefused() =>
        Assert.Throws<ArgumentNullException>(() => WaymarkDataOptions.FromConfiguration(null!));

    [Fact]
    public void TheDefaultDirectoryEndsInWaymark() =>
        Assert.Equal("Waymark", Path.GetFileName(WaymarkDataOptions.DefaultDataDirectory));
}
