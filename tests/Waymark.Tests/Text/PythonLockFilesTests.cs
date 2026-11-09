using Waymark.Core.Text;
using Waymark.Tests.Support;

namespace Waymark.Tests.Text;

public sealed class PythonLockFilesTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static string Packages(string table, params (string Name, string? Version)[] packages) =>
        string.Join("\n", packages.Select(p => $"[[{table}]]\nname = '{p.Name}'" + (p.Version is null ? string.Empty : $"\nversion = '{p.Version}'")));

    [Theory]
    [InlineData("Flask_SQLAlchemy", "flask-sqlalchemy")]
    [InlineData("zope.interface", "zope-interface")]
    [InlineData("a__b--c..d", "a-b-c-d")]
    [InlineData("a-_.b", "a-b")]
    [InlineData("  Pkg  ", "pkg")]
    [InlineData("requests", "requests")]
    public void NormalizeFollowsPep503(string name, string expected) =>
        Assert.Equal(expected, PythonLockFiles.Normalize(name));

    [Fact]
    public void NormalizeRefusesNull() => Assert.Throws<ArgumentNullException>(() => PythonLockFiles.Normalize(null!));

    [Theory]
    [InlineData("uv.lock", "uv")]
    [InlineData("sub/poetry.lock", "poetry")]
    [InlineData("pdm.lock", "pdm")]
    [InlineData("pylock.toml", "pylock")]
    [InlineData("UV.LOCK", "uv")]
    [InlineData("something.toml", "pylock")]
    public void ToolOfNamesTheToolBehindALockFile(string lockFile, string expected) =>
        Assert.Equal(expected, PythonLockFiles.ToolOf(lockFile));

    [Fact]
    public void TheKnownLockFilesAreListedInPreferenceOrder() =>
        Assert.Equal(["uv.lock", "poetry.lock", "pdm.lock", "pylock.toml"], PythonLockFiles.Names);

    [Fact]
    public async Task ReadsTheExactVersionsOfEveryNamedPackage()
    {
        using var dir = new TempDirectory();
        dir.Write("proj/uv.lock", Packages("package", ("Requests", "2.31.0"), ("Flask_SQLAlchemy", "3.1.1"), ("novers", null)));

        var pins = await PythonLockFiles.ReadAsync(dir.Root, dir.PathOf("proj"), _ct);

        Assert.NotNull(pins);
        Assert.Equal("proj/uv.lock", pins.LockFile);
        Assert.Equal(2, pins.Versions.Count);
        Assert.Equal("2.31.0", pins.Versions["requests"]);
        Assert.Equal("3.1.1", pins.Versions["flask-sqlalchemy"]);
    }

    [Fact]
    public async Task WalksUpFromTheProjectFolderToTheWorkspaceRoot()
    {
        using var dir = new TempDirectory();
        dir.Write("uv.lock", Packages("package", ("a", "1.0")));
        Directory.CreateDirectory(dir.PathOf("x/y"));

        var pins = await PythonLockFiles.ReadAsync(dir.Root, dir.PathOf("x/y"), _ct);

        Assert.NotNull(pins);
        Assert.Equal("uv.lock", pins.LockFile);
    }

    [Fact]
    public async Task TheNearestLockFileWins()
    {
        using var dir = new TempDirectory();
        dir.Write("uv.lock", Packages("package", ("a", "1.0")));
        dir.Write("proj/poetry.lock", Packages("package", ("a", "2.0")));

        var pins = await PythonLockFiles.ReadAsync(dir.Root, dir.PathOf("proj"), _ct);

        Assert.NotNull(pins);
        Assert.Equal("proj/poetry.lock", pins.LockFile);
        Assert.Equal("2.0", pins.Versions["a"]);
    }

    [Fact]
    public async Task InOneFolderUvIsPreferredOverPoetry()
    {
        using var dir = new TempDirectory();
        dir.Write("poetry.lock", Packages("package", ("a", "2.0")));
        dir.Write("uv.lock", Packages("package", ("a", "1.0")));

        var pins = await PythonLockFiles.ReadAsync(dir.Root, dir.Root, _ct);

        Assert.NotNull(pins);
        Assert.Equal("uv.lock", pins.LockFile);
        Assert.Equal("1.0", pins.Versions["a"]);
    }

    [Fact]
    public async Task PylockUsesThePackagesTable()
    {
        using var dir = new TempDirectory();
        dir.Write("pylock.toml", Packages("packages", ("Django", "5.0")));

        var pins = await PythonLockFiles.ReadAsync(dir.Root, dir.Root, _ct);

        Assert.NotNull(pins);
        Assert.Equal("5.0", pins.Versions["django"]);
    }

    [Fact]
    public async Task OtherTablesAreIgnoredButTheLockFileIsStillReported()
    {
        using var dir = new TempDirectory();
        dir.Write("uv.lock", "version = 1\n[package]\nname = 'single'\nversion = '1'\n[[dependency]]\nname = 'other'\nversion = '2'\n");

        var pins = await PythonLockFiles.ReadAsync(dir.Root, dir.Root, _ct);

        Assert.NotNull(pins);
        Assert.Equal("uv.lock", pins.LockFile);
        Assert.Empty(pins.Versions);
    }

    [Fact]
    public async Task NoLockFileGivesNull()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.PathOf("proj"));

        Assert.Null(await PythonLockFiles.ReadAsync(dir.Root, dir.PathOf("proj"), _ct));
    }

    [Fact]
    public async Task AFolderOutsideTheWorkspaceIsNotSearched()
    {
        using var workspace = new TempDirectory();
        using var other = new TempDirectory();
        other.Write("uv.lock", Packages("package", ("a", "1.0")));

        Assert.Null(await PythonLockFiles.ReadAsync(workspace.Root, other.Root, _ct));
    }

    [Fact]
    public async Task BlankArgumentsAreRefused()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => PythonLockFiles.ReadAsync("", "x", _ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => PythonLockFiles.ReadAsync("x", " ", _ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => PythonLockFiles.ReadAsync(null!, "x", _ct));
    }
}
