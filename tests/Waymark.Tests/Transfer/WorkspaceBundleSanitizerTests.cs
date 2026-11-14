using Waymark.Core.Memory;
using Waymark.Core.Security;
using Waymark.Core.Transfer;
using Waymark.Core.Workspaces;

namespace Waymark.Tests.Transfer;

public sealed class WorkspaceBundleSanitizerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static WorkspaceBundle Bundle(params BundleWorkspace[] workspaces) => new("whatever", 99, Now, workspaces);

    private static BundleWorkspace Ws(string name, IReadOnlyList<BundleRule>? rules = null, IReadOnlyList<BundleMap>? maps = null, string? path = null, bool enabled = true) =>
        new(name, path, enabled, rules ?? [], maps ?? []);

    private static BundleRule Rule(AccessRuleKind kind, string pattern, string note = "", bool enabled = true) => new(kind, pattern, note, enabled, Now);

    private static BundleComponent Step(string spec, int depth = 0, string link = "call", string note = "") => new(spec, note, depth, link);

    private static BundleMap Flow(string name, params BundleComponent[] steps) => new(name, "summary", Now, steps, Ordered: true);

    private static BundleMap Listed(string name, params string[] specs) =>
        new(name, "summary", Now, [.. specs.Select(spec => new BundleComponent(spec, string.Empty))], Ordered: false);

    private static BundleWorkspace Sanitized(SanitizedBundle result) => Assert.Single(result.Bundle.Workspaces);

    private static SanitizedBundle Run(BundleWorkspace workspace) => WorkspaceBundleSanitizer.Sanitize(Bundle(workspace));

    [Fact]
    public void ACleanWorkspacePassesWithoutNotes()
    {
        var result = Run(Ws("app", enabled: false));

        Assert.Empty(result.Notes);
        var workspace = Sanitized(result);
        Assert.Equal("app", workspace.Name);
        Assert.False(workspace.Enabled);
        Assert.Empty(workspace.AccessRules);
        Assert.Empty(workspace.FeatureMaps);
    }

    [Fact]
    public void TheHeaderIsAlwaysRewrittenToTheCurrentFormat()
    {
        var result = Run(Ws("app"));

        Assert.Equal(WorkspaceBundleJson.FormatName, result.Bundle.Format);
        Assert.Equal(WorkspaceBundleJson.CurrentVersion, result.Bundle.FormatVersion);
        Assert.Equal(Now, result.Bundle.ExportedUtc);
    }

    [Fact]
    public void NullBundleIsRefused() => Assert.Throws<ArgumentNullException>(() => WorkspaceBundleSanitizer.Sanitize(null!));

    [Fact]
    public void NullEntriesAreSkippedWithoutANote()
    {
        var result = WorkspaceBundleSanitizer.Sanitize(Bundle(null!, Ws("app")));

        Assert.Equal("app", Sanitized(result).Name);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void ANameIsTrimmed() => Assert.Equal("app", Sanitized(Run(Ws("  app  "))).Name);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("bad name!")]
    [InlineData("-leading")]
    public void AWorkspaceWithABadNameIsLeftOutWithTheReason(string name)
    {
        var result = Run(Ws(name));

        Assert.Empty(result.Bundle.Workspaces);
        Assert.StartsWith("A workspace was left out: ", Assert.Single(result.Notes));
    }

    [Fact]
    public void AMissingNameIsLeftOut()
    {
        var result = Run(new BundleWorkspace(null!, null, true, [], []));

        Assert.Empty(result.Bundle.Workspaces);
        Assert.Equal("A workspace was left out: Give the workspace a name.", Assert.Single(result.Notes));
    }

    [Fact]
    public void ASecondWorkspaceWithTheSameNameIgnoringCaseIsLeftOut()
    {
        var result = WorkspaceBundleSanitizer.Sanitize(Bundle(Ws("app", enabled: true), Ws("APP", enabled: false)));

        var kept = Sanitized(result);
        Assert.True(kept.Enabled);
        Assert.Equal("The workspace 'APP' is in the file twice; the second one was left out.", Assert.Single(result.Notes));
    }

    [Fact]
    public void AtMostOneHundredWorkspacesAreKept()
    {
        var many = Enumerable.Range(0, WorkspaceBundleSanitizer.MaxWorkspaces + 1).Select(i => Ws($"ws{i}")).ToArray();

        var result = WorkspaceBundleSanitizer.Sanitize(Bundle(many));

        Assert.Equal(100, result.Bundle.Workspaces.Count);
        Assert.Equal("The file has more than 100 workspaces; the rest was left out.", Assert.Single(result.Notes));
    }

    [Fact]
    public void NotesAreCappedAtFortyWithACountOfTheRest()
    {
        var bad = Enumerable.Range(0, 45).Select(i => Ws($"!bad{i}")).ToArray();

        var result = WorkspaceBundleSanitizer.Sanitize(Bundle(bad));

        Assert.Equal(41, result.Notes.Count);
        Assert.Equal("... and 5 more.", result.Notes[^1]);
    }

    [Theory]
    [InlineData("  C:\\src\\app  ", "C:\\src\\app")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void APathHintIsTrimmedOrDropped(string? path, string? expected) =>
        Assert.Equal(expected, Sanitized(Run(Ws("app", path: path))).PathHint);

    [Fact]
    public void APathHintThatIsTooLongOrHasControlCharactersIsDropped()
    {
        // The limit is 260 on Windows and 1024 elsewhere, so the test follows the rule rather than one platform's number.
        var limit = WorkspacePathRules.MaxPathLength;
        Assert.Null(Sanitized(Run(Ws("app", path: new string('a', limit + 1)))).PathHint);
        Assert.Null(Sanitized(Run(Ws("app", path: "C:\\x\ty"))).PathHint);
        Assert.NotNull(Sanitized(Run(Ws("app", path: new string('a', limit)))).PathHint);
    }

    [Fact]
    public void AValidRuleIsKeptWithATrimmedNoteItsSwitchAndItsDate()
    {
        var result = Run(Ws("app", [Rule(AccessRuleKind.BlockFileName, "*.sql", "  why  ", enabled: false)]));

        var rule = Assert.Single(Sanitized(result).AccessRules);
        Assert.Equal(AccessRuleKind.BlockFileName, rule.Kind);
        Assert.Equal("*.sql", rule.Pattern);
        Assert.Equal("why", rule.Note);
        Assert.False(rule.Enabled);
        Assert.Equal(Now, rule.CreatedUtc);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void ARuleOfAnUnknownKindIsLeftOut()
    {
        var result = Run(Ws("app", [Rule((AccessRuleKind)99, "x")]));

        Assert.Empty(Sanitized(result).AccessRules);
        Assert.Equal("app: an access rule was left out (unknown rule type).", Assert.Single(result.Notes));
    }

    [Fact]
    public void ARuleWithAnEmptyPatternIsLeftOut()
    {
        var result = Run(Ws("app", [Rule(AccessRuleKind.BlockKeyword, string.Empty)]));

        Assert.Empty(Sanitized(result).AccessRules);
        Assert.StartsWith("app: an access rule was left out (", Assert.Single(result.Notes));
    }

    [Fact]
    public void ARuleWithAnUnacceptableNoteIsLeftOut()
    {
        var result = Run(Ws("app", [Rule(AccessRuleKind.BlockKeyword, "secret", new string('n', 10_000))]));

        Assert.Empty(Sanitized(result).AccessRules);
        Assert.Contains("its note is not acceptable", Assert.Single(result.Notes));
    }

    [Fact]
    public void ADuplicateRuleKeepsTheFirst()
    {
        var result = Run(Ws("app", [Rule(AccessRuleKind.BlockKeyword, "secret", enabled: true), Rule(AccessRuleKind.BlockKeyword, "SECRET", enabled: false)]));

        var rule = Assert.Single(Sanitized(result).AccessRules);
        Assert.True(rule.Enabled);
    }

    [Fact]
    public void RulesBeyondTheLimitAreLeftOutWithANote()
    {
        var rules = Enumerable.Range(0, AccessRuleRules.MaxRulesPerWorkspace + 1).Select(i => Rule(AccessRuleKind.BlockKeyword, $"keyword{i}")).ToArray();

        var result = Run(Ws("app", rules));

        Assert.Equal(AccessRuleRules.MaxRulesPerWorkspace, Sanitized(result).AccessRules.Count);
        Assert.Equal($"app: more than {AccessRuleRules.MaxRulesPerWorkspace} access rules; the rest was left out.", Assert.Single(result.Notes));
    }

    [Fact]
    public void AMapNameIsNormalised() =>
        Assert.Equal("order-flow", Assert.Single(Sanitized(Run(Ws("app", maps: [Listed("Order Flow", "a.cs")]))).FeatureMaps).Name);

    [Fact]
    public void AMapWithABadNameIsLeftOut()
    {
        var result = Run(Ws("app", maps: [Listed("!!", "a.cs")]));

        Assert.Empty(Sanitized(result).FeatureMaps);
        Assert.Equal("app: a feature map was left out (its name is not 1 to 60 lowercase letters, digits and '-').", Assert.Single(result.Notes));
    }

    [Fact]
    public void TwoMapsWithTheSameNormalisedNameKeepTheFirst()
    {
        var result = Run(Ws("app", maps: [Listed("a-flow", "first.cs"), Listed("A Flow", "second.cs")]));

        var map = Assert.Single(Sanitized(result).FeatureMaps);
        Assert.Equal("first.cs", Assert.Single(map.Components).Spec);
    }

    [Fact]
    public void ASummaryIsTrimmedAndOverlongOnesLeaveTheMapOut()
    {
        var good = new BundleMap("m", "  short  ", null, [new BundleComponent("a.cs", string.Empty)], false);
        var tooLong = new BundleMap("n", new string('s', 301), null, [new BundleComponent("a.cs", string.Empty)], false);

        var result = Run(Ws("app", maps: [good, tooLong]));

        var kept = Assert.Single(Sanitized(result).FeatureMaps);
        Assert.Equal("short", kept.Summary);
        Assert.Equal("app: the feature map 'n' was left out (its summary is longer than 300 characters).", Assert.Single(result.Notes));
    }

    [Fact]
    public void AListMapKeepsCanonicalComponentsOnceEachWithoutDepthOrLink()
    {
        var map = new BundleMap(
            "m",
            "s",
            null,
            [new BundleComponent("src\\A.cs", "  n  ", 3, "ui"), new BundleComponent("src/A.cs", string.Empty), new BundleComponent("b.cs", string.Empty)],
            false);

        var kept = Assert.Single(Sanitized(Run(Ws("app", maps: [map]))).FeatureMaps);

        Assert.False(kept.Ordered);
        Assert.Equal([new BundleComponent("src/A.cs", "n"), new BundleComponent("b.cs", string.Empty)], kept.Components);
    }

    [Fact]
    public void AListMapWithABadComponentIsLeftOutWithTheReason()
    {
        var result = Run(Ws("app", maps: [Listed("m", "a.cs", "not a spec")]));

        Assert.Empty(Sanitized(result).FeatureMaps);
        var note = Assert.Single(result.Notes);
        Assert.StartsWith("app: the feature map 'm' was left out (", note);
        Assert.Contains("Not a component", note);
    }

    [Fact]
    public void AListMapNeedsBetweenOneAndThirtyComponents()
    {
        var thirty = Listed("thirty", [.. Enumerable.Range(0, 30).Select(i => $"f{i}.cs")]);
        var thirtyOne = Listed("over", [.. Enumerable.Range(0, 31).Select(i => $"f{i}.cs")]);
        var none = Listed("none");

        var result = Run(Ws("app", maps: [thirty, thirtyOne, none]));

        Assert.Equal("thirty", Assert.Single(Sanitized(result).FeatureMaps).Name);
        Assert.Equal(2, result.Notes.Count);
        Assert.Contains("this one has 31", result.Notes[0]);
        Assert.Contains("this one has 0", result.Notes[1]);
    }

    [Fact]
    public void AComponentNoteThatIsTooLongOrHasControlCharactersLeavesTheMapOut()
    {
        var longNote = new BundleMap("a", "s", null, [new BundleComponent("a.cs", new string('n', 121))], false);
        var control = new BundleMap("b", "s", null, [new BundleComponent("a.cs", "bad\u0001note")], false);

        var result = Run(Ws("app", maps: [longNote, control]));

        Assert.Empty(Sanitized(result).FeatureMaps);
        Assert.Equal(2, result.Notes.Count);
        Assert.All(result.Notes, note => Assert.Contains("control characters", note));
    }

    [Fact]
    public void AValidFlowIsKeptWithCanonicalStepsDepthsAndLinks()
    {
        var flow = Flow("order", Step("src\\A.cs:A.Run", note: "  entry  "), Step("src/B.cs:B.Do", 1, "EVENT"), Step("src/C.cs:C.Save", 2));

        var result = Run(Ws("app", maps: [flow]));

        var kept = Assert.Single(Sanitized(result).FeatureMaps);
        Assert.True(kept.Ordered);
        Assert.Equal(
            [
                new BundleComponent("src/A.cs:A.Run", "entry", 0, "call"),
                new BundleComponent("src/B.cs:B.Do", string.Empty, 1, "event"),
                new BundleComponent("src/C.cs:C.Save", string.Empty, 2, "call"),
            ],
            kept.Components);
        Assert.Empty(result.Notes);
    }

    [Theory]
    [InlineData("bogus", 1)]
    [InlineData("call", 9)]
    [InlineData("call", -1)]
    public void AFlowStepWithAnInvalidLinkOrDepthLeavesTheFlowOut(string link, int depth)
    {
        var flow = Flow("order", Step("a.cs"), Step("b.cs", depth, link));

        var result = Run(Ws("app", maps: [flow]));

        Assert.Empty(Sanitized(result).FeatureMaps);
        Assert.Contains("a step has a link or a depth that is not valid", Assert.Single(result.Notes));
    }

    [Theory]
    [InlineData("first", "the first line is the trigger and has no indent")]
    [InlineData("twoRoots", "a flow has one trigger")]
    [InlineData("skipped", "indented 2 levels")]
    [InlineData("duplicate", "already a step")]
    [InlineData("empty", "A flow needs at least one step.")]
    public void AFlowThatBreaksTheFlowGrammarIsLeftOutWithTheFirstReason(string scenario, string expectedReason)
    {
        var flow = scenario switch
        {
            "first" => Flow("x", Step("a.cs", 1)),
            "twoRoots" => Flow("x", Step("a.cs"), Step("b.cs")),
            "skipped" => Flow("x", Step("a.cs"), Step("b.cs", 2)),
            "duplicate" => Flow("x", Step("a.cs"), Step("b.cs", 1), Step("B.CS", 1)),
            _ => Flow("x"),
        };

        var result = Run(Ws("app", maps: [flow]));

        Assert.Empty(Sanitized(result).FeatureMaps);
        var note = Assert.Single(result.Notes);
        Assert.StartsWith("app: the feature map 'x' was left out (", note);
        Assert.Contains(expectedReason, note);
    }

    [Fact]
    public void AFlowStepNoteWithAControlCharacterLeavesTheFlowOut()
    {
        var result = Run(Ws("app", maps: [Flow("x", Step("a.cs", note: "bad\u0001"))]));

        Assert.Empty(Sanitized(result).FeatureMaps);
        Assert.Contains("control characters", Assert.Single(result.Notes));
    }

    [Fact]
    public void AtMostAThousandMapsAreKeptPerWorkspace()
    {
        var maps = Enumerable.Range(0, WorkspaceBundleSanitizer.MaxMapsPerWorkspace + 1).Select(i => Listed($"m{i}", "a.cs")).ToArray();

        var result = Run(Ws("app", maps: maps));

        Assert.Equal(1000, Sanitized(result).FeatureMaps.Count);
        Assert.Equal("app: more than 1000 feature maps; the rest was left out.", Assert.Single(result.Notes));
    }

    [Fact]
    public void ASanitizedBundleSurvivesTheJsonRoundTripUnchanged()
    {
        var bundle = Bundle(Ws("app", [Rule(AccessRuleKind.BlockKeyword, "secret", "why")], [Flow("order", Step("a.cs"), Step("b.cs", 1, "http"))], path: @"C:\src\app"));
        var clean = WorkspaceBundleSanitizer.Sanitize(bundle).Bundle;

        var text = WorkspaceBundleJson.Serialize(clean);

        Assert.True(WorkspaceBundleJson.TryParse(text, out var parsed, out var error), error);
        Assert.Equal(text, WorkspaceBundleJson.Serialize(WorkspaceBundleSanitizer.Sanitize(parsed).Bundle));
    }
}
