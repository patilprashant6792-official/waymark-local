using Waymark.Data.Sqlite;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class DatabaseFileModesTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheDatabaseAndItsWalAndShmBecomeOwnerOnlyOffWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var database = await TestDatabase.CreateAsync(_ct);
        await using var connection = await database.Connections.OpenAsync(_ct);
        const UnixFileMode Readable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        var files = DatabaseFileModes.FilesOf(database.Options.DatabasePath);
        Assert.Contains(database.Options.DatabasePath, files);
        foreach (var file in files)
        {
            File.SetUnixFileMode(file, Readable);
        }

        foreach (var file in files)
        {
            DatabaseFileModes.MakeOwnerOnly(file);
        }

        foreach (var file in files)
        {
            Assert.Equal(DatabaseFileModes.OwnerOnly, File.GetUnixFileMode(file));
        }
    }

    [Fact]
    public void OnlyFilesThatExistAreListed()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("waymark.db", string.Empty);

        Assert.Equal([path], DatabaseFileModes.FilesOf(path));
    }
}
