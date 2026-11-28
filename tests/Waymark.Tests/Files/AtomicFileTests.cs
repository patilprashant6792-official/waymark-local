using Waymark.Core.Platform;
using Waymark.Modules.Files;
using Waymark.Tests.Support;

namespace Waymark.Tests.Files;

public sealed class AtomicFileTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheNewContentReplacesTheOldAndNoTemporaryFileOrFolderStays()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("a.txt", "old");

        await AtomicFile.ReplaceAsync(path, "new"u8.ToArray(), _ct);

        Assert.Equal("new", await File.ReadAllTextAsync(path, _ct));
        Assert.Equal(["a.txt"], Directory.GetFiles(temp.Root).Select(Path.GetFileName));
        Assert.Empty(Directory.GetDirectories(temp.Root));
    }

    [Fact]
    public async Task ANewFileIsWrittenTooAndNoScratchFolderStays()
    {
        using var temp = new TempDirectory();
        var path = temp.PathOf("new.txt");

        await AtomicFile.ReplaceAsync(path, "hello"u8.ToArray(), _ct);

        Assert.Equal("hello", await File.ReadAllTextAsync(path, _ct));
        Assert.Empty(Directory.GetDirectories(temp.Root));
    }

    [Theory]
    [InlineData(".waymark-0123/content.tmp", true)]
    [InlineData("src/.waymark-abcd", true)]
    [InlineData("src\\.waymark-abcd\\content.tmp", true)]
    [InlineData("src/waymark-notes.md", false)]
    [InlineData("src/Program.cs", false)]
    public void ScratchPathsAreRecognised(string relative, bool expected) =>
        Assert.Equal(expected, ScratchPaths.IsScratch(relative));

    [Fact]
    public async Task AnEditedScriptKeepsItsExecuteBitOffWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var path = temp.Write("run.sh", "echo old\n");
        const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        File.SetUnixFileMode(path, Executable);

        await AtomicFile.ReplaceAsync(path, "echo new\n"u8.ToArray(), _ct);

        Assert.Equal(Executable, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task APrivateFileStaysPrivateOffWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var path = temp.Write("notes.md", "old");
        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(path, OwnerOnly);

        await AtomicFile.ReplaceAsync(path, "new"u8.ToArray(), _ct);

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
    }
}
