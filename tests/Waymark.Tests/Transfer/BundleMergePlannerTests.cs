using Waymark.Core.Memory;
using Waymark.Core.Security;
using Waymark.Core.Transfer;

namespace Waymark.Tests.Transfer;

public sealed class BundleMergePlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static AccessRule Existing(AccessRuleKind kind, string pattern, bool enabled = true, long id = 1) =>
        new(id, 1, kind, pattern, enabled, string.Empty, Now);

    private static BundleRule Incoming(AccessRuleKind kind, string pattern, bool enabled = true) =>
        new(kind, pattern, string.Empty, enabled, null);

    private static FeatureMap Stored(string name, bool ordered, params FeatureMapStep[] steps) =>
        new(1, name, "s", 1, ordered, string.Empty, Now, steps);

    private static BundleMap Map(string name, bool ordered, params BundleComponent[] components) =>
        new(name, "summary", null, components, ordered);

    private static BundleWorkspace Workspace(IReadOnlyList<BundleRule>? rules = null, IReadOnlyList<BundleMap>? maps = null) =>
        new("app", null, true, rules ?? [], maps ?? []);

    private static WorkspaceMergePlan Plan(BundleWorkspace incoming, BundleContent content = BundleContent.All, AccessRule[]? rules = null, FeatureMap[]? maps = null) =>
        BundleMergePlanner.Plan(incoming, content, rules ?? [], maps ?? []);

    [Fact]
    public void NothingToAddGivesAnEmptyPlan()
    {
        var plan = Plan(Workspace());

        Assert.False(plan.HasChanges);
        Assert.Empty(plan.RulesToAdd);
        Assert.Empty(plan.Maps);
        Assert.Equal(0, plan.StepsToAdd);
        Assert.Equal(0, plan.MapsNew);
        Assert.False(WorkspaceMergePlan.Empty.HasChanges);
    }

    [Fact]
    public void ANewRuleIsAdded()
    {
        var rule = Incoming(AccessRuleKind.BlockKeyword, "secret");

        var plan = Plan(Workspace([rule]));

        Assert.Equal([rule], plan.RulesToAdd);
        Assert.Equal(0, plan.RulesAlreadyThere);
        Assert.True(plan.HasChanges);
    }

    [Fact]
    public void ARuleThatIsThereAlreadyIsCountedAndNotAdded()
    {
        var plan = Plan(
            Workspace([Incoming(AccessRuleKind.BlockKeyword, "SECRET")]),
            rules: [Existing(AccessRuleKind.BlockKeyword, "secret")]);

        Assert.Empty(plan.RulesToAdd);
        Assert.Equal(1, plan.RulesAlreadyThere);
        Assert.Equal(0, plan.RulesDifferent);
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void ARuleThatIsThereWithAnotherSwitchIsCountedAsDifferentAndLeftAsTheOwnerHasIt()
    {
        var plan = Plan(
            Workspace([Incoming(AccessRuleKind.BlockKeyword, "secret", enabled: false)]),
            rules: [Existing(AccessRuleKind.BlockKeyword, "secret", enabled: true)]);

        Assert.Empty(plan.RulesToAdd);
        Assert.Equal(1, plan.RulesAlreadyThere);
        Assert.Equal(1, plan.RulesDifferent);
    }

    [Fact]
    public void TheSamePatternOfAnotherKindIsANewRule()
    {
        var plan = Plan(
            Workspace([Incoming(AccessRuleKind.BlockFileName, "secret")]),
            rules: [Existing(AccessRuleKind.BlockKeyword, "secret")]);

        Assert.Single(plan.RulesToAdd);
        Assert.Equal(0, plan.RulesAlreadyThere);
    }

    [Fact]
    public void OnlyEnabledFileTypeRulesCountAsWideningWhatClaudeCanRead()
    {
        var plan = Plan(Workspace(
        [
            Incoming(AccessRuleKind.AllowExtension, ".proto"),
            Incoming(AccessRuleKind.AllowExtension, ".thrift", enabled: false),
            Incoming(AccessRuleKind.BlockKeyword, "secret"),
        ]));

        Assert.Equal(3, plan.RulesToAdd.Count);
        Assert.Equal(1, plan.RulesAllowingFileTypes);
    }

    [Fact]
    public void RulesBeyondTheLimitAreCountedAndLeftOut()
    {
        var almostFull = Enumerable.Range(0, AccessRuleRules.MaxRulesPerWorkspace - 1)
            .Select(i => Existing(AccessRuleKind.BlockKeyword, $"kw{i}", id: i + 1))
            .ToArray();

        var plan = Plan(
            Workspace([Incoming(AccessRuleKind.BlockFolder, "one"), Incoming(AccessRuleKind.BlockFolder, "two"), Incoming(AccessRuleKind.BlockFolder, "three")]),
            rules: almostFull);

        Assert.Single(plan.RulesToAdd);
        Assert.Equal(2, plan.RulesOverLimit);
    }

    [Fact]
    public void ARuleThatIsThereAlreadyDoesNotCountAgainstTheLimit()
    {
        var full = Enumerable.Range(0, AccessRuleRules.MaxRulesPerWorkspace)
            .Select(i => Existing(AccessRuleKind.BlockKeyword, $"kw{i}", id: i + 1))
            .ToArray();

        var plan = Plan(Workspace([Incoming(AccessRuleKind.BlockKeyword, "kw0")]), rules: full);

        Assert.Equal(1, plan.RulesAlreadyThere);
        Assert.Equal(0, plan.RulesOverLimit);
    }

    [Fact]
    public void ANewMapIsAddedWithAllItsSteps()
    {
        var map = Map("order", true, new BundleComponent("a.cs", string.Empty), new BundleComponent("b.cs", "n", 1, "event"));

        var plan = Plan(Workspace(maps: [map]));

        var added = Assert.Single(plan.Maps);
        Assert.Equal("order", added.Name);
        Assert.Equal("summary", added.Summary);
        Assert.True(added.Ordered);
        Assert.Equal(map.Components, added.Steps);
        Assert.Equal(1, plan.MapsNew);
        Assert.Equal(2, plan.StepsToAdd);
        Assert.True(plan.HasChanges);
    }

    [Fact]
    public void AMapWithTheSameNameAndStepsIsUnchangedIgnoringCase()
    {
        var plan = Plan(
            Workspace(maps: [Map("Order", true, new BundleComponent("A.cs", string.Empty), new BundleComponent("b.cs", string.Empty, 1, "EVENT"))]),
            maps: [Stored("order", true, new FeatureMapStep("a.cs", 0, "call", string.Empty), new FeatureMapStep("b.cs", 1, "event", string.Empty))]);

        Assert.Empty(plan.Maps);
        Assert.Equal(1, plan.MapsUnchanged);
        Assert.Equal(0, plan.MapsConflicting);
        Assert.False(plan.HasChanges);
    }

    [Theory]
    [InlineData("other.cs", 0, "call")]
    [InlineData("a.cs", 1, "call")]
    [InlineData("a.cs", 0, "http")]
    public void AnOrderedMapWithADifferentStepConflictsAndIsLeftOut(string spec, int depth, string link)
    {
        var plan = Plan(
            Workspace(maps: [Map("order", true, new BundleComponent(spec, string.Empty, depth, link))]),
            maps: [Stored("order", true, new FeatureMapStep("a.cs", 0, "call", string.Empty))]);

        Assert.Empty(plan.Maps);
        Assert.Equal(1, plan.MapsConflicting);
        Assert.Equal(0, plan.MapsUnchanged);
    }

    [Fact]
    public void AMapWithAnotherNumberOfStepsConflicts()
    {
        var plan = Plan(
            Workspace(maps: [Map("order", true, new BundleComponent("a.cs", string.Empty))]),
            maps: [Stored("order", true, new FeatureMapStep("a.cs", 0, "call", string.Empty), new FeatureMapStep("b.cs", 1, "call", string.Empty))]);

        Assert.Equal(1, plan.MapsConflicting);
    }

    [Fact]
    public void AnOrderedMapNeverEqualsAnUnorderedOne()
    {
        var plan = Plan(
            Workspace(maps: [Map("order", true, new BundleComponent("a.cs", string.Empty))]),
            maps: [Stored("order", false, new FeatureMapStep("a.cs", 0, "call", string.Empty))]);

        Assert.Equal(1, plan.MapsConflicting);
    }

    [Fact]
    public void UnorderedMapsAreEqualWhenTheyHoldTheSameComponentsInAnyOrder()
    {
        var same = Plan(
            Workspace(maps: [Map("list", false, new BundleComponent("b.cs", string.Empty), new BundleComponent("a.cs", string.Empty))]),
            maps: [Stored("list", false, new FeatureMapStep("a.cs", 0, "call", string.Empty), new FeatureMapStep("b.cs", 0, "call", string.Empty))]);
        var different = Plan(
            Workspace(maps: [Map("list", false, new BundleComponent("c.cs", string.Empty), new BundleComponent("a.cs", string.Empty))]),
            maps: [Stored("list", false, new FeatureMapStep("a.cs", 0, "call", string.Empty), new FeatureMapStep("b.cs", 0, "call", string.Empty))]);

        Assert.Equal(1, same.MapsUnchanged);
        Assert.Equal(1, different.MapsConflicting);
    }

    [Fact]
    public void OnlyTheChosenContentIsPlanned()
    {
        var workspace = Workspace(
            [Incoming(AccessRuleKind.BlockKeyword, "secret")],
            [Map("order", true, new BundleComponent("a.cs", string.Empty))]);

        var rulesOnly = Plan(workspace, BundleContent.AccessRules);
        var mapsOnly = Plan(workspace, BundleContent.FeatureMaps);
        var nothing = Plan(workspace, BundleContent.None);

        Assert.Single(rulesOnly.RulesToAdd);
        Assert.Empty(rulesOnly.Maps);
        Assert.Empty(mapsOnly.RulesToAdd);
        Assert.Single(mapsOnly.Maps);
        Assert.False(nothing.HasChanges);
    }

    [Fact]
    public void NullArgumentsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => BundleMergePlanner.Plan(null!, BundleContent.All, [], []));
        Assert.Throws<ArgumentNullException>(() => BundleMergePlanner.Plan(Workspace(), BundleContent.All, null!, []));
        Assert.Throws<ArgumentNullException>(() => BundleMergePlanner.Plan(Workspace(), BundleContent.All, [], null!));
    }
}
