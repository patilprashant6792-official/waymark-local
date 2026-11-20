using Waymark.Core.Memory;
using Waymark.Modules.Code.Memory;
using Waymark.Modules.Code.Memory.Semantic;

namespace Waymark.Tests.Memory;

public sealed class FlowWordScorerTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AFlowThatHasEveryWordRanksFirst()
    {
        var guide = Map(1, "waymark-guide-delivery", "Attaches the guide", "src/Mcp/ToolGuideDelivery.cs:ToolGuideDelivery.AttachAsync");
        var audit = Map(2, "tool-call-audit", "Writes the audit row", "src/Mcp/ToolPipeline.cs:ToolPipeline.RecordAsync");

        var ranked = FlowWordScorer.Rank([audit, guide], NoteFtsQuery.Words("guide delivery"), 10);

        Assert.Equal(1, ranked[0].FlowId);
        Assert.DoesNotContain(ranked, match => match.FlowId == 2);
    }

    [Fact]
    public void TheStepWhoseMemberMatchedIsNamed()
    {
        var guide = Map(1, "waymark-guide-delivery", "Attaches the guide", "src/Mcp/ToolGuideDelivery.cs:ToolGuideDelivery.AttachAsync");

        var match = FlowWordScorer.Score(guide, NoteFtsQuery.Words("AttachAsync"));

        Assert.True(match.Score > 0);
        Assert.Equal(["src/Mcp/ToolGuideDelivery.cs:ToolGuideDelivery.AttachAsync"], match.Steps);
    }

    [Fact]
    public void NoWordsMatchNothing() =>
        Assert.Empty(FlowWordScorer.Rank([Map(1, "a", "b", "c.cs:C.D")], [], 10));

    private static FeatureMap Map(long id, string name, string summary, string spec) =>
        new(id, name, summary, 1, true, string.Empty, At, [new FeatureMapStep(spec, 0, "call", string.Empty)]);
}
