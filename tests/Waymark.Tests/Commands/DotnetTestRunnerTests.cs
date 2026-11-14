using Waymark.Modules.Commands.Recipes;
using Waymark.Tests.Support;

namespace Waymark.Tests.Commands;

/// <summary>The test action names its target with --solution or --project only when global.json switches dotnet test to Microsoft.Testing.Platform.</summary>
public sealed class DotnetTestRunnerTests
{
    [Theory]
    [InlineData("{ \"test\": { \"runner\": \"Microsoft.Testing.Platform\" } }", true)]
    [InlineData("{ // comment\n \"sdk\": { \"version\": \"10.0.100\" }, \"test\": { \"runner\": \"Microsoft.Testing.Platform\", }, }", true)]
    [InlineData("{ \"test\": { \"runner\": \"VSTest\" } }", false)]
    [InlineData("{ \"sdk\": { \"version\": \"10.0.100\" } }", false)]
    [InlineData("{ \"test\": \"Microsoft.Testing.Platform\" }", false)]
    [InlineData("{ \"test\": { \"runner\": 1 } }", false)]
    [InlineData("not json", false)]
    [InlineData("[]", false)]
    public void TheRunnerIsReadFromGlobalJson(string globalJson, bool expected)
    {
        using var folder = new TempDirectory();
        folder.Write("global.json", globalJson);

        Assert.Equal(expected, DotnetTestRunner.UsesTestingPlatform(folder.Root));
    }

    [Fact]
    public void AGlobalJsonInAParentFolderCounts()
    {
        using var folder = new TempDirectory();
        folder.Write("global.json", "{ \"test\": { \"runner\": \"Microsoft.Testing.Platform\" } }");
        Directory.CreateDirectory(folder.PathOf("src/app"));

        Assert.True(DotnetTestRunner.UsesTestingPlatform(folder.PathOf("src/app")));
    }

    [Fact]
    public void TheNearestGlobalJsonWins()
    {
        using var folder = new TempDirectory();
        folder.Write("global.json", "{ \"test\": { \"runner\": \"Microsoft.Testing.Platform\" } }");
        folder.Write("app/global.json", "{ \"sdk\": { \"version\": \"10.0.100\" } }");

        Assert.False(DotnetTestRunner.UsesTestingPlatform(folder.PathOf("app")));
    }
}
