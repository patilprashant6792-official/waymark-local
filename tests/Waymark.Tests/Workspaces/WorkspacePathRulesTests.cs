using Waymark.Core.Workspaces;

namespace Waymark.Tests.Workspaces;

public sealed class WorkspacePathRulesTests
{
    [Theory]
    [InlineData("app")]
    [InlineData("My-App_1.2")]
    [InlineData("9lives")]
    [InlineData("a")]
    public void GoodNamesAreAccepted(string name) => Assert.Null(WorkspacePathRules.ValidateName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingNameAsksForOne(string? name) =>
        Assert.Equal("Give the workspace a name.", WorkspacePathRules.ValidateName(name));

    [Theory]
    [InlineData("*")]
    [InlineData("-leading")]
    [InlineData(".leading")]
    [InlineData("_leading")]
    [InlineData("has space")]
    [InlineData(" padded")]
    [InlineData("slash/name")]
    [InlineData("\u00e4pfel")]
    public void BadNamesAreRefusedWithTheRule(string name) =>
        Assert.Contains("letters, digits, dots, dashes or underscores", WorkspacePathRules.ValidateName(name));

    [Fact]
    public void NameLengthLimitIsInclusive()
    {
        Assert.Null(WorkspacePathRules.ValidateName(new string('a', WorkspacePathRules.MaxNameLength)));
        Assert.NotNull(WorkspacePathRules.ValidateName(new string('a', WorkspacePathRules.MaxNameLength + 1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AnEmptyPathIsRefused(string? path)
    {
        var (normalized, error) = WorkspacePathRules.NormalizePath(path);

        Assert.Null(normalized);
        Assert.Equal("Folder path cannot be empty.", error);
    }

    [Fact]
    public void AnOverlongPathIsRefused()
    {
        var (normalized, error) = WorkspacePathRules.NormalizePath(new string('a', WorkspacePathRules.MaxPathLength + 1));

        Assert.Null(normalized);
        Assert.Contains("longer than 260", error);
    }

    [Theory]
    [InlineData("C:/a*b")]
    [InlineData("C:/a<b")]
    [InlineData("C:/a|b")]
    [InlineData("C:/a$b")]
    [InlineData("C:/a%b")]
    [InlineData("C:/a;b")]
    [InlineData("C:/a=b")]
    [InlineData("C:/a!b")]
    [InlineData("C:/a\"b")]
    public void CharactersOutsideTheAllowedSetAreRefused(string path)
    {
        var (normalized, error) = WorkspacePathRules.NormalizePath(path);

        Assert.Null(normalized);
        Assert.Contains("characters that are not allowed", error);
    }

    [Fact]
    public void ARelativePathAsksForTheFullPath()
    {
        var (normalized, error) = WorkspacePathRules.NormalizePath("src/app");

        Assert.Null(normalized);
        Assert.StartsWith("Use the full path", error);
    }

    [Theory]
    [InlineData(@"C:\Users\you\app", @"C:\Users\you\app")]
    [InlineData(@"C:\Users\you\app\", @"C:\Users\you\app")]
    [InlineData(@"  C:\Users\you\app  ", @"C:\Users\you\app")]
    [InlineData(@"C:/Users/you/app", @"C:\Users\you\app")]
    [InlineData(@"C:\a\..\b", @"C:\b")]
    [InlineData(@"C:\Users\Zoë (work)\my app [v2]", @"C:\Users\Zoë (work)\my app [v2]")]
    public void AFullPathIsCanonicalisedWithoutATrailingSeparator(string path, string expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (normalized, error) = WorkspacePathRules.NormalizePath(path);

        Assert.Null(error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:/")]
    [InlineData(@"C:\a\..")]
    public void AWholeDriveIsRefused(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (normalized, error) = WorkspacePathRules.NormalizePath(path);

        Assert.Null(normalized);
        Assert.Contains("not a whole drive", error);
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("OBJ")]
    [InlineData("node_modules")]
    [InlineData(".git")]
    [InlineData(".venv")]
    [InlineData("__pycache__")]
    public void BuildAndDependencyFoldersAreExcludedIgnoringCase(string folder) =>
        Assert.Contains(folder, WorkspacePathRules.ExcludedDirectories);

    [Theory]
    [InlineData("src")]
    [InlineData("tests")]
    [InlineData("docs")]
    public void SourceFoldersAreNotExcluded(string folder) =>
        Assert.DoesNotContain(folder, WorkspacePathRules.ExcludedDirectories);
}
