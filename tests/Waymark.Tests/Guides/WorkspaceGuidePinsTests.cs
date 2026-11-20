using Waymark.Core.Guides;

namespace Waymark.Tests.Guides;

public sealed class WorkspaceGuidePinsTests
{
    private static readonly WorkspaceGuideText Text = WorkspaceGuideText.Empty;

    [Fact]
    public void WithoutPinsTheHashIsTheOldOne() =>
        Assert.Equal(WorkspaceGuideRules.Hash("app", Text), WorkspaceGuideRules.Hash("app", Text, []));

    [Fact]
    public void APinChangesTheHash() =>
        Assert.NotEqual(WorkspaceGuideRules.Hash("app", Text), WorkspaceGuideRules.Hash("app", Text, ["41:abc"]));

    [Fact]
    public void AnEditedPinChangesTheHash() =>
        Assert.NotEqual(WorkspaceGuideRules.Hash("app", Text, ["41:abc"]), WorkspaceGuideRules.Hash("app", Text, ["41:def"]));
}
