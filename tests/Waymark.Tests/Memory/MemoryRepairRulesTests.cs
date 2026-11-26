using Waymark.Core.Guides;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;
using Waymark.Modules.Code.Memory.Repairs;
using Waymark.Tests.Support;

namespace Waymark.Tests.Memory;

public sealed class MemoryRepairRulesTests
{
    private static readonly WorkspaceHead OnMain = new(true, "main");
    private static readonly WorkspaceHead Detached = new(true, null);

    [Fact]
    public void AFoundStepIsHereWhateverBranchItWasSavedOn()
    {
        Assert.Equal(StepPlace.Here, MemoryRepairText.Judge(true, true, "feature/x", OnMain));
        Assert.Equal(StepPlace.Here, MemoryRepairText.Judge(true, true, null, WorkspaceHead.NotGit));
    }

    [Fact]
    public void AMissingStepLastSeenOnThisBranchIsGone() =>
        Assert.Equal(StepPlace.Gone, MemoryRepairText.Judge(true, false, "main", OnMain));

    [Theory]
    [InlineData("feature/x")]
    [InlineData(null)]
    public void AMissingStepLastSeenElsewhereOrNeverIsKeptForItsBranch(string? savedOn) =>
        Assert.Equal(StepPlace.OtherBranch, MemoryRepairText.Judge(true, false, savedOn, OnMain));

    [Fact]
    public void ADetachedHeadNeverCallsAStepGone() =>
        Assert.Equal(StepPlace.OtherBranch, MemoryRepairText.Judge(true, false, "main", Detached));

    [Fact]
    public void WithoutGitAMissingStepIsGone() =>
        Assert.Equal(StepPlace.Gone, MemoryRepairText.Judge(true, false, null, WorkspaceHead.NotGit));

    [Fact]
    public void AStepThatWasNotLookedAtIsNotJudged() =>
        Assert.Equal(StepPlace.NotInspected, MemoryRepairText.Judge(false, false, "main", OnMain));

    [Theory]
    [InlineData(MemoryRepairKind.LinkBroken)]
    [InlineData(MemoryRepairKind.StepGone)]
    [InlineData(MemoryRepairKind.StepMoved)]
    [InlineData(MemoryRepairKind.NoteCodeChanged)]
    [InlineData(MemoryRepairKind.NoteFlowChanged)]
    public void KindsRoundTripThroughTheirStoredText(MemoryRepairKind kind) =>
        Assert.Equal(kind, MemoryRepairText.KindFrom(MemoryRepairText.Kind(kind)));

    [Theory]
    [InlineData("automatic", MemoryRepairMode.Automatic)]
    [InlineData("ASK", MemoryRepairMode.Ask)]
    [InlineData("off", MemoryRepairMode.Off)]
    [InlineData("nonsense", MemoryRepairMode.Automatic)]
    [InlineData(null, MemoryRepairMode.Automatic)]
    public void TheModeIsReadForgivinglyAndDefaultsToAutomatic(string? text, MemoryRepairMode expected) =>
        Assert.Equal(expected, MemoryRepairOptions.ModeFrom(text));

    [Fact]
    public void TheDefaultsAreAutomaticWithAutoFixAndABudget()
    {
        Assert.Equal(MemoryRepairMode.Automatic, MemoryRepairOptions.Default.Mode);
        Assert.True(MemoryRepairOptions.Default.AutoFix);
        Assert.Equal(MemoryRepairOptions.DefaultBudget, MemoryRepairOptions.Default.Budget);
    }

    [Theory]
    [InlineData("src/A.cs:A.Run", "src/A.cs")]
    [InlineData("src/A.cs#L10-20", "src/A.cs")]
    [InlineData("./src\\A.cs", "src/A.cs")]
    [InlineData("/docs/x.md", "docs/x.md")]
    [InlineData("", "")]
    public void ALedgerPathIsTheFileOnly(string spec, string expected) =>
        Assert.Equal(expected, MemoryRepairLedger.FileOf(spec));

    [Fact]
    public void TheLedgerRemembersReadsPerClientAndWorkspace()
    {
        var ledger = new MemoryRepairLedger(new StepClock(DateTimeOffset.UnixEpoch));

        ledger.RecordRead("claude-ai", "Waymark", "src/A.cs");

        Assert.True(ledger.HasRead("claude-ai", "waymark", "src/A.cs:A.Run"));
        Assert.False(ledger.HasRead("claude-code", "Waymark", "src/A.cs"));
        Assert.False(ledger.HasRead("claude-ai", "Other", "src/A.cs"));
        Assert.False(ledger.HasRead("claude-ai", "Waymark", "src/B.cs"));
    }

    [Fact]
    public void TheLedgerForgetsAfterTheSessionWentIdle()
    {
        var clock = new StepClock(DateTimeOffset.UnixEpoch);
        var ledger = new MemoryRepairLedger(clock);
        ledger.RecordRead("c", "w", "src/A.cs");
        ledger.RecordRepair("c", "w");

        clock.Advance(MemoryRepairLedger.IdleTime + TimeSpan.FromMinutes(1));

        Assert.False(ledger.HasRead("c", "w", "src/A.cs"));
        Assert.Equal(0, ledger.RepairsDone("c", "w"));
    }

    [Fact]
    public void TheLedgerCountsRepairs()
    {
        var ledger = new MemoryRepairLedger(new StepClock(DateTimeOffset.UnixEpoch));

        ledger.RecordRepair("c", "w");
        ledger.RecordRepair("c", "w");

        Assert.Equal(2, ledger.RepairsDone("c", "w"));
        Assert.Equal(0, ledger.RepairsDone("c", "other"));
    }

    [Fact]
    public void TheServerInstructionsTellAgentsToFollowTheRepairModeAndStayShort()
    {
        Assert.Contains("repairs", GuideRules.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("automatic", GuideRules.ServerInstructions, StringComparison.Ordinal);
        Assert.True(GuideRules.ServerInstructions.Length < 4_000);
    }

    [Theory]
    [InlineData(MemoryRepairMode.Automatic, "without asking")]
    [InlineData(MemoryRepairMode.Ask, "after a yes")]
    [InlineData(MemoryRepairMode.Off, "owner repairs")]
    public void TheModeLineSaysWhatTheAgentMayDo(MemoryRepairMode mode, string expected) =>
        Assert.Contains(expected, RepairRecallService.ModeLine(mode, 5, 1), StringComparison.Ordinal);
}
