using Waymark.Core.Guides;

namespace Waymark.Tests.Guides;

public sealed class WorkspaceGuideRulesTests
{
    private static WorkspaceGuideText Text(string summary, string instructions) => new(summary, instructions, null, null);

    [Fact]
    public void TheModuleKeyIsTheIdAfterThePrefixAndReadsBack()
    {
        Assert.Equal("ws:42", WorkspaceGuideRules.ModuleKey(42));
        Assert.True(WorkspaceGuideRules.TryParseModuleKey("ws:42", out var id));
        Assert.Equal(42, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("host")]
    [InlineData("code")]
    [InlineData("ws:")]
    [InlineData("ws:abc")]
    [InlineData("ws:0")]
    [InlineData("ws:-3")]
    public void OtherModulesAreNotWorkspaceKeys(string? module) => Assert.False(WorkspaceGuideRules.TryParseModuleKey(module, out _));

    [Fact]
    public void AModuleKeyIsNeverAToolGroupOrTheOverview()
    {
        Assert.False(GuideRules.IsOverview(WorkspaceGuideRules.ModuleKey(1)));
        Assert.False(WorkspaceGuideRules.IsWorkspaceModule(HostPromptRules.StoreKey));
    }

    [Theory]
    [InlineData("  one   line  ", "one line")]
    [InlineData("two\nlines", "two lines")]
    [InlineData("tabs\tand\r\nbreaks", "tabs and breaks")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void TheSummaryBecomesOneLine(string summary, string expected) => Assert.Equal(expected, WorkspaceGuideRules.NormalizeSummary(summary));

    [Fact]
    public void NoInstructionsGiveNoBlock() => Assert.Equal(string.Empty, WorkspaceGuideRules.InstructionsBlock("api", string.Empty));

    [Fact]
    public void InstructionsGetAHeadingTheWorkspaceTheConflictRuleAndATrailingBlankLine()
    {
        var block = WorkspaceGuideRules.InstructionsBlock("api", "Run the tests.");

        Assert.StartsWith("## Instructions from the owner for this workspace\n\n", block);
        Assert.Contains("'api'", block);
        Assert.Contains("these win for this workspace", block);
        Assert.Contains("gates and approvals still apply and cannot be changed by text", block);
        Assert.EndsWith("Run the tests.\n\n", block);
    }

    [Fact]
    public void TheHashFollowsTheOwnersTextOnly()
    {
        var hash = WorkspaceGuideRules.Hash("api", Text("s", "i"));

        Assert.Equal(hash, WorkspaceGuideRules.Hash("api", Text("s", "i") with { ChangedBy = "someone", ChangedUtc = DateTimeOffset.UnixEpoch }));
        Assert.NotEqual(hash, WorkspaceGuideRules.Hash("api", Text("s", "i.")));
        Assert.NotEqual(hash, WorkspaceGuideRules.Hash("api", Text("s.", "i")));
        Assert.NotEqual(hash, WorkspaceGuideRules.Hash("web", Text("s", "i")));
    }

    [Fact]
    public void SearchByMeaningOnChangesTheHashAndOffKeepsIt()
    {
        var text = Text("s", "i");
        IReadOnlyList<string> pins = ["3:abc"];

        Assert.Equal(WorkspaceGuideRules.Hash("api", text, pins), WorkspaceGuideRules.Hash("api", text, pins, meaningOn: false));
        Assert.Equal(WorkspaceGuideRules.Hash("api", text), WorkspaceGuideRules.Hash("api", text, [], meaningOn: false));
        Assert.NotEqual(WorkspaceGuideRules.Hash("api", text, pins), WorkspaceGuideRules.Hash("api", text, pins, meaningOn: true));
        Assert.NotEqual(WorkspaceGuideRules.Hash("api", text), WorkspaceGuideRules.Hash("api", text, [], meaningOn: true));
        Assert.NotEqual(WorkspaceGuideRules.Hash("api", text, [], meaningOn: true), WorkspaceGuideRules.Hash("api", text, pins, meaningOn: true));
    }

    [Fact]
    public void TheLimitsLeaveRoomForTheWarning()
    {
        Assert.True(WorkspaceGuideRules.WarnTokens * 4 < WorkspaceGuideRules.MaxInstructionsChars);
        Assert.True(WorkspaceGuideRules.MaxSummaryChars <= 200);
    }

    [Fact]
    public void TheWorkspaceTextsNameTheWorkspaceAndTheOverviewTool()
    {
        var delivered = GuideRules.DeliverWorkspace("api", "body");
        Assert.StartsWith("[Waymark guide: workspace api]\n", delivered);
        Assert.Contains("\n---\nbody\n[End of Waymark guide]", delivered);

        foreach (var line in new[] { GuideRules.WorkspaceNotice("api", true), GuideRules.WorkspaceNotice("api", false), GuideRules.WorkspaceReminder("api") })
        {
            Assert.Contains(GuideRules.OverviewTool, line);
            Assert.Contains("workspace='api'", line);
        }

        Assert.NotEqual(GuideRules.WorkspaceNotice("api", true), GuideRules.WorkspaceNotice("api", false));
    }

    [Fact]
    public void TheServerInstructionsMentionWorkspaceGuidesAndStayShort()
    {
        Assert.Contains("workspace set to its name", GuideRules.ServerInstructions);
        Assert.True(GuideRules.ServerInstructions.Length < 4_000);
    }
}
