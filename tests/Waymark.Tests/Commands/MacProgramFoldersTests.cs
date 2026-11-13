using Waymark.Modules.Commands.Execution;

namespace Waymark.Tests.Commands;

public sealed class MacProgramFoldersTests
{
    private const string Home = "/Users/you";

    [Fact]
    public void HomebrewComesFirstForEveryProgram()
    {
        var folders = ExecutableLocator.MacFolders("git", Home).ToList();

        Assert.Equal(["/opt/homebrew/bin", "/usr/local/bin"], folders);
    }

    [Fact]
    public void DotnetAlsoLooksWhereItsInstallersPutIt()
    {
        var folders = ExecutableLocator.MacFolders("dotnet", Home).ToList();

        Assert.Contains("/usr/local/share/dotnet", folders);
        Assert.Contains(Path.Combine(Home, ".dotnet"), folders);
    }

    [Fact]
    public void PythonAlsoLooksAtPyenvAndThePythonOrgInstaller()
    {
        var folders = ExecutableLocator.MacFolders("python3", Home).ToList();

        Assert.Contains(Path.Combine(Home, ".pyenv", "shims"), folders);
        Assert.Contains("/Library/Frameworks/Python.framework/Versions/Current/bin", folders);
    }

    [Fact]
    public void NodeAlsoLooksAtVolta()
    {
        Assert.Contains(Path.Combine(Home, ".volta", "bin"), ExecutableLocator.MacFolders("node", Home));
    }

    [Fact]
    public void WithoutAHomeOnlyTheSharedFoldersAreListed()
    {
        var folders = ExecutableLocator.MacFolders("dotnet", null).ToList();

        Assert.All(folders, folder => Assert.StartsWith("/", folder));
        Assert.DoesNotContain(folders, folder => folder.Contains(".dotnet", StringComparison.Ordinal));
    }

    [Fact]
    public void ZshIsTheAutomaticShellOnMac()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var shells = new ShellLocator(new ExecutableLocator());

        Assert.Equal("zsh", shells.Resolve(null)?.Id);
        Assert.Equal(ShellKind.Bash, shells.Resolve("zsh")?.Kind);
    }
}
