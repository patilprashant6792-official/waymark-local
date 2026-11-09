using System.Text.RegularExpressions;
using Waymark.Modules.Commands.Recipes;

namespace Waymark.Tests.Commands;

/// <summary>These patterns are the only thing standing between free text from the AI and a command line, so each one is pinned with values it must accept and values it must refuse.</summary>
public sealed class RecipeRulesTests
{
    private static readonly string[] Injection =
    [
        "a;b", "a&b", "a|b", "a`b`", "$(x)", "a\"b", "a'b", "a\nb", "a\rb", "a\0b", "a>b", "a<b", "a*b", "a?b", "a%b",
    ];

    private static void AssertAccepts(Regex rule, params string[] values)
    {
        foreach (var value in values)
        {
            Assert.True(rule.IsMatch(value), "should accept: " + value);
        }
    }

    private static void AssertRejects(Regex rule, params string[] values)
    {
        foreach (var value in values)
        {
            Assert.False(rule.IsMatch(value), "should reject: " + value);
        }
    }

    [Fact]
    public void PackageIds()
    {
        AssertAccepts(RecipeRules.PackageId(), "Newtonsoft.Json", "a", "My-Pkg_1", "9lives", new string('a', 100));
        AssertRejects(RecipeRules.PackageId(), string.Empty, "-rf", ".hidden", "_x", "a b", "a/b", "a\\b", new string('a', 101));
        AssertRejects(RecipeRules.PackageId(), Injection);
    }

    [Fact]
    public void PackageVersions()
    {
        AssertAccepts(RecipeRules.PackageVersion(), "1", "1.0.0", "1.0.0-beta.1", "1.0.0+build", "10.0.0-rc.2.25502.107");
        AssertRejects(RecipeRules.PackageVersion(), string.Empty, "v1", "-1", "1 0", "1.0;x", "[1.0,2.0)", new string('1', 51));
        AssertRejects(RecipeRules.PackageVersion(), Injection);
    }

    [Fact]
    public void Configurations()
    {
        AssertAccepts(RecipeRules.Configuration(), "Debug", "Release", "My_Config", new string('a', 32));
        AssertRejects(RecipeRules.Configuration(), string.Empty, "1x", "-c", "De bug", "Debug;calc", "a-b", new string('a', 33));
        AssertRejects(RecipeRules.Configuration(), Injection);
    }

    [Fact]
    public void Frameworks()
    {
        AssertAccepts(RecipeRules.Framework(), "net10.0", "netstandard2.0", "net8.0-windows", "ab");
        AssertRejects(RecipeRules.Framework(), string.Empty, "n", "10net", "net 8", "-f", "net8.0;x", new string('a', 41));
        AssertRejects(RecipeRules.Framework(), Injection);
    }

    [Fact]
    public void ProjectNames()
    {
        AssertAccepts(RecipeRules.ProjectName(), "My.Project", "_x", "Waymark.Tests-1");
        AssertRejects(RecipeRules.ProjectName(), string.Empty, "1x", "-x", ".x", "a b", "a/b", "a\\b");
        AssertRejects(RecipeRules.ProjectName(), Injection);
    }

    [Fact]
    public void TestFilters()
    {
        AssertAccepts(
            RecipeRules.TestFilter(),
            "FullyQualifiedName~Foo.Bar",
            "Category=Fast&Priority!=1",
            "(A|B)",
            "Name~Tests, Name!~Slow",
            "a");
        AssertRejects(RecipeRules.TestFilter(), string.Empty, "a;b", "$(x)", "a`b", "a\"b", "a'b", "a\nb", "a:b", "a/b", new string('a', 201));
    }

    [Fact]
    public void MigrationAndContextNames()
    {
        AssertAccepts(RecipeRules.MigrationName(), "AddUsers", "_x", "Init_2");
        AssertRejects(RecipeRules.MigrationName(), string.Empty, "1a", "a-b", "a.b", "a b");
        AssertAccepts(RecipeRules.ContextName(), "AppDbContext", "My.Ctx");
        AssertRejects(RecipeRules.ContextName(), string.Empty, "1a", "a-b", "a b", ".x");
    }

    [Fact]
    public void LaunchProfiles()
    {
        AssertAccepts(RecipeRules.LaunchProfile(), "https", "IIS Express", "My.Profile-1");
        AssertRejects(RecipeRules.LaunchProfile(), string.Empty, " lead", "-x", ".x", "a;b", "a/b");
    }

    [Fact]
    public void GitNames()
    {
        AssertAccepts(RecipeRules.GitBranch(), "main", "feature/x-1", "release/1.2", "v1.0");
        AssertRejects(RecipeRules.GitBranch(), string.Empty, "-x", "/x", ".x", "a b", "a;b", "a~b", "a^b", "a:b", "a@{1}");
        AssertAccepts(RecipeRules.GitRevision(), "HEAD", "HEAD~2", "main^", "HEAD@{1}", "abc123", "origin/main");
        AssertRejects(RecipeRules.GitRevision(), string.Empty, "-x", "--upload-pack=x", "a b", "a;b", "a:b");
        AssertAccepts(RecipeRules.GitRemote(), "origin", "up-stream.1");
        AssertRejects(RecipeRules.GitRemote(), string.Empty, "-x", "a/b", "a b", "https://x");
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("feature/login", true)]
    [InlineData("a..b", false)]
    [InlineData("a//b", false)]
    [InlineData("topic/", false)]
    [InlineData("topic.", false)]
    [InlineData("topic.lock", false)]
    [InlineData("TOPIC.LOCK", false)]
    [InlineData("-delete", false)]
    [InlineData("", false)]
    public void SafeBranchNamesFollowGitsOwnRules(string name, bool expected) =>
        Assert.Equal(expected, RecipeRules.IsSafeBranchName(name));

    [Fact]
    public void PythonValues()
    {
        AssertAccepts(RecipeRules.PythonVersion(), "1.2.3", "2.0.0rc1", "1!2.0", "1.0+local", "1_0");
        AssertRejects(RecipeRules.PythonVersion(), string.Empty, "v1", "-1", "1 2", "1;2");
        AssertAccepts(RecipeRules.PythonGroup(), "dev", "test-extras", "a.b_c");
        AssertRejects(RecipeRules.PythonGroup(), string.Empty, "1a", "-x", "a b", "_x");
        AssertAccepts(RecipeRules.PythonModule(), "pytest", "pkg.sub.mod", "_private", "a.b.c.d.e.f.g.h.i.j");
        AssertRejects(RecipeRules.PythonModule(), string.Empty, ".x", "a..b", "a-b", "1a", "a.b.c.d.e.f.g.h.i.j.k", "a b");
        AssertAccepts(RecipeRules.PythonVersionRequest(), "3", "3.12", "3.12.4");
        AssertRejects(RecipeRules.PythonVersionRequest(), string.Empty, "2.7", "3.", "3.123", "3.12.4.1", "3.x", "-3");
    }

    [Fact]
    public void PytestValues()
    {
        AssertAccepts(RecipeRules.PytestExpression(), "slow and not integration", "test_foo", "(a or b)", "param[1]", new string('a', 200));
        AssertRejects(RecipeRules.PytestExpression(), string.Empty, "-x", " x", "a;b", "$x", "a&b", "a|b", "a`b", new string('a', 201));
        AssertAccepts(RecipeRules.PytestNodeId(), "TestClass::test_name[param]", "test_x", "Test::test[a-b,c=1]");
        AssertRejects(RecipeRules.PytestNodeId(), string.Empty, "-x", "::x", "1a", "a;b", "a`b", "a$b");
    }

    [Fact]
    public void VirtualEnvironmentFolders()
    {
        AssertAccepts(RecipeRules.FolderName(), ".venv", "venv", "env-1_x");
        AssertRejects(RecipeRules.FolderName(), string.Empty, "a/b", "a\\b", "a b", "a;b", new string('a', 51));
    }

    /// <summary>Characterization, not endorsement: the folder pattern matches the dot-only names. If the recipe does not refuse them further down, a '..' would step out of the project, so a change here is worth a look.</summary>
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void DotOnlyFolderNamesCurrentlyMatch(string name) =>
        Assert.Matches(RecipeRules.FolderName(), name);

    /// <summary>Characterization: '$' also matches before a final line feed, so a trailing newline passes the pattern. Callers that build a command line from these values should keep treating the value as one argument.</summary>
    [Theory]
    [InlineData("Release\n")]
    public void ATrailingLineFeedCurrentlyPassesTheAnchor(string value) =>
        Assert.Matches(RecipeRules.Configuration(), value);

    [Fact]
    public void NpmValues()
    {
        AssertAccepts(RecipeRules.NpmScriptName(), "build", "test:unit", "lint.fix", "a1");
        AssertRejects(RecipeRules.NpmScriptName(), string.Empty, "-x", ":x", "a b", "a;b", "a/b", new string('a', 61));
        AssertAccepts(RecipeRules.NpmPackageName(), "left-pad", "@types/node", "a", "a.b_c~d");
        AssertRejects(RecipeRules.NpmPackageName(), string.Empty, "Left-Pad", ".x", "_x", "-x", "@scope", "@scope/", "a b", "a;b", "a/b", "@a/b/c", "git+https://x");
        AssertAccepts(RecipeRules.NpmExactVersion(), "1.2.3", "1.2.3-beta.1", "1.2.3+build.5", "1.2.3-rc.1+b.2");
        AssertRejects(RecipeRules.NpmExactVersion(), string.Empty, "1.2", "^1.2.3", "~1.2.3", "latest", "1.2.3 ", ">=1.0.0", "1.2.3;x", "v1.2.3");
    }
}
