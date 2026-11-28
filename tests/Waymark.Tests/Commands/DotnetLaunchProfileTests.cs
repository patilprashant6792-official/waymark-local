using Waymark.Modules.Commands.Recipes;
using Waymark.Tests.Replace;

namespace Waymark.Tests.Commands;

/// <summary>run and watch start the app like the developer's own dotnet run: with the project's default launch profile unless told otherwise.</summary>
public sealed class DotnetLaunchProfileTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public DotnetLaunchProfileTests() => _workspace.Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />"u8.ToArray());

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData("run")]
    [InlineData("watch")]
    public void TheDefaultProfileIsUsedWhenNoneIsNamed(string action)
    {
        var args = Plan(action, "{\"project\":\"App/App.csproj\"}").Arguments;

        Assert.DoesNotContain("--no-launch-profile", args);
        Assert.DoesNotContain("--launch-profile", args);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("watch")]
    public void ANamedProfileIsPassed(string action)
    {
        var args = Plan(action, "{\"project\":\"App/App.csproj\",\"launchProfile\":\"https\"}").Arguments.ToList();

        var at = args.IndexOf("--launch-profile");
        Assert.True(at >= 0);
        Assert.Equal("https", args[at + 1]);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("watch")]
    public void NoLaunchProfileStartsTheAppBare(string action)
    {
        var args = Plan(action, "{\"project\":\"App/App.csproj\",\"noLaunchProfile\":true}").Arguments;

        Assert.Contains("--no-launch-profile", args);
        Assert.DoesNotContain("--launch-profile", args);
    }

    [Fact]
    public void WatchPassesTheProfileToDotnetWatchBeforeRun()
    {
        var args = Plan("watch", "{\"project\":\"App/App.csproj\",\"launchProfile\":\"http\"}").Arguments.ToList();

        Assert.True(args.IndexOf("--launch-profile") < args.IndexOf("run"));
    }

    [Fact]
    public void ANamedProfileAndNoProfileTogetherAreRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => Plan("run", "{\"project\":\"App/App.csproj\",\"launchProfile\":\"http\",\"noLaunchProfile\":true}"));

        Assert.Contains("not both", ex.Message, StringComparison.Ordinal);
    }

    private CommandPlan Plan(string action, string optionsJson) =>
        DotnetRecipes.Plan(action, new RecipeContext(_workspace.Workspace, new FolderPathGuard(_workspace.Workspace), RecipeOptions.Parse(optionsJson)));
}
