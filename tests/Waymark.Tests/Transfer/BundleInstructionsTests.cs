using Waymark.Core.Guides;
using Waymark.Core.Transfer;

namespace Waymark.Tests.Transfer;

public sealed class BundleInstructionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static BundleWorkspace Ws(BundleInstructions? instructions) => new("app", null, true, [], [], instructions);

    private static WorkspaceGuideText Here(string summary, string instructions) => new(summary, instructions, Now, "admin");

    [Fact]
    public void InstructionsAreSetOnAWorkspaceWithoutText()
    {
        var incoming = new BundleInstructions("API", "Run the tests.");

        var plan = BundleMergePlanner.Plan(Ws(incoming), BundleContent.All, [], [], WorkspaceGuideText.Empty);

        Assert.Same(incoming, plan.InstructionsToSet);
        Assert.False(plan.InstructionsKept);
        Assert.True(plan.HasChanges);
    }

    [Fact]
    public void InstructionsAreSetOnANewWorkspace()
    {
        var plan = BundleMergePlanner.Plan(Ws(new BundleInstructions("API", "x")), BundleContent.All, [], []);

        Assert.NotNull(plan.InstructionsToSet);
    }

    [Fact]
    public void TheOwnersTextHereIsNeverReplaced()
    {
        var plan = BundleMergePlanner.Plan(Ws(new BundleInstructions("API", "other")), BundleContent.All, [], [], Here("API", "mine"));

        Assert.Null(plan.InstructionsToSet);
        Assert.True(plan.InstructionsKept);
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void TheSameTextIsHereAlready()
    {
        var plan = BundleMergePlanner.Plan(Ws(new BundleInstructions("API", "mine")), BundleContent.All, [], [], Here("API", "mine"));

        Assert.Null(plan.InstructionsToSet);
        Assert.False(plan.InstructionsKept);
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void InstructionsAreLeftAloneWhenTheImportIsAboutSomethingElse()
    {
        var plan = BundleMergePlanner.Plan(Ws(new BundleInstructions("API", "x")), BundleContent.AccessRules | BundleContent.FeatureMaps, [], []);

        Assert.Null(plan.InstructionsToSet);
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void TheSanitizerCleansInstructions()
    {
        var result = WorkspaceBundleSanitizer.Sanitize(new WorkspaceBundle("x", 2, Now, [Ws(new BundleInstructions("  API \n server ", "\r\nRun the tests.\r\n"))]));

        var instructions = Assert.Single(result.Bundle.Workspaces).Instructions;
        Assert.NotNull(instructions);
        Assert.Equal("API server", instructions.Summary);
        Assert.Equal("Run the tests.", instructions.Text);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void TheSanitizerDropsEmptyInstructionsQuietly()
    {
        var result = WorkspaceBundleSanitizer.Sanitize(new WorkspaceBundle("x", 2, Now, [Ws(new BundleInstructions(" ", "\n"))]));

        Assert.Null(Assert.Single(result.Bundle.Workspaces).Instructions);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void TheSanitizerDropsInstructionsWithASecretAndSaysSo()
    {
        var token = "ghp_" + new string('A', 36);

        var result = WorkspaceBundleSanitizer.Sanitize(new WorkspaceBundle("x", 2, Now, [Ws(new BundleInstructions("API", "use " + token))]));

        Assert.Null(Assert.Single(result.Bundle.Workspaces).Instructions);
        Assert.Contains(result.Notes, note => note.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSanitizerDropsInstructionsOverTheLimitAndSaysSo()
    {
        var result = WorkspaceBundleSanitizer.Sanitize(new WorkspaceBundle("x", 2, Now, [Ws(new BundleInstructions("API", new string('a', WorkspaceGuideRules.MaxInstructionsChars + 1)))]));

        Assert.Null(Assert.Single(result.Bundle.Workspaces).Instructions);
        Assert.Contains(result.Notes, note => note.Contains("longer", StringComparison.Ordinal));
    }

    [Fact]
    public void InstructionsTravelInTheFileAndAFileWithoutThemStillReads()
    {
        var bundle = new WorkspaceBundle(WorkspaceBundleJson.FormatName, WorkspaceBundleJson.CurrentVersion, Now, [Ws(new BundleInstructions("API", "Run the tests."))]);

        Assert.True(WorkspaceBundleJson.TryParse(WorkspaceBundleJson.Serialize(bundle), out var read, out var error), error);
        Assert.Equal(new BundleInstructions("API", "Run the tests."), Assert.Single(read.Workspaces).Instructions);

        var without = "{\"format\":\"waymark-workspace-bundle\",\"formatVersion\":2,\"exportedUtc\":\"2026-01-01T00:00:00Z\"," +
            "\"workspaces\":[{\"name\":\"app\",\"pathHint\":null,\"enabled\":true,\"accessRules\":[],\"featureMaps\":[]}]}";
        Assert.True(WorkspaceBundleJson.TryParse(without, out var old, out error), error);
        Assert.Null(Assert.Single(old.Workspaces).Instructions);
    }
}
