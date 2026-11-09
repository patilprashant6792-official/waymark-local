using Waymark.Core.Security;
using Waymark.Core.Transfer;

namespace Waymark.Tests.Transfer;

public sealed class WorkspaceBundleJsonTests
{
    private static readonly DateTimeOffset Exported = new(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private static string Json(string text) => text.Replace('\'', '"');

    private static string Header(int version) =>
        $"'format':'waymark-workspace-bundle','formatVersion':{version},'exportedUtc':'2026-01-01T00:00:00Z'";

    private static WorkspaceBundle Sample() => new(
        WorkspaceBundleJson.FormatName,
        WorkspaceBundleJson.CurrentVersion,
        Exported,
        [
            new BundleWorkspace(
                "app",
                @"C:\src\app",
                true,
                [new BundleRule(AccessRuleKind.BlockFolder, "legacy", "old code", false, Exported)],
                [
                    new BundleMap(
                        "order-flow",
                        "Place an order",
                        Exported,
                        [new BundleComponent("src/A.cs:A.Run", "entry"), new BundleComponent("src/B.cs:B.Do", string.Empty, 1, "event")],
                        true),
                ]),
        ]);

    [Fact]
    public void ABundleSurvivesAWriteAndARead()
    {
        var text = WorkspaceBundleJson.Serialize(Sample());

        Assert.True(WorkspaceBundleJson.TryParse(text, out var bundle, out var error), error);
        Assert.Null(error);
        Assert.Equal(WorkspaceBundleJson.FormatName, bundle.Format);
        Assert.Equal(2, bundle.FormatVersion);
        Assert.Equal(Exported, bundle.ExportedUtc);
        var workspace = Assert.Single(bundle.Workspaces);
        Assert.Equal("app", workspace.Name);
        Assert.Equal(@"C:\src\app", workspace.PathHint);
        Assert.True(workspace.Enabled);
        var rule = Assert.Single(workspace.AccessRules);
        Assert.Equal(new BundleRule(AccessRuleKind.BlockFolder, "legacy", "old code", false, Exported), rule);
        var map = Assert.Single(workspace.FeatureMaps);
        Assert.Equal("order-flow", map.Name);
        Assert.Equal("Place an order", map.Summary);
        Assert.True(map.Ordered);
        Assert.Equal(
            [new BundleComponent("src/A.cs:A.Run", "entry"), new BundleComponent("src/B.cs:B.Do", string.Empty, 1, "event")],
            map.Components);
    }

    [Fact]
    public void TheTextUsesCamelCaseNamesAndNamedEnumValues()
    {
        var text = WorkspaceBundleJson.Serialize(Sample());

        Assert.Contains("\"formatVersion\": 2", text);
        Assert.Contains("\"kind\": \"BlockFolder\"", text);
        Assert.Contains("\"pathHint\"", text);
    }

    [Fact]
    public void SerializeRefusesNull() => Assert.Throws<ArgumentNullException>(() => WorkspaceBundleJson.Serialize(null!));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    public void EmptyTextIsNotABundle(string? text)
    {
        Assert.False(WorkspaceBundleJson.TryParse(text, out var bundle, out var error));
        Assert.Null(bundle);
        Assert.Equal("The file is empty.", error);
    }

    [Fact]
    public void TooLargeTextIsRefusedBeforeItIsParsed()
    {
        Assert.False(WorkspaceBundleJson.TryParse(new string('x', WorkspaceBundleJson.MaxChars + 1), out _, out var error));
        Assert.Equal("The file is too large to be a Waymark bundle.", error);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("12")]
    [InlineData("plain text")]
    public void TextThatIsNotJsonOfTheExpectedShapeIsExplained(string text)
    {
        Assert.False(WorkspaceBundleJson.TryParse(text, out var bundle, out var error));
        Assert.Null(bundle);
        Assert.Contains("not valid JSON of the expected shape", error);
    }

    [Fact]
    public void ANumberWhereAnEnumNameIsExpectedIsRefused()
    {
        var text = Json("{" + Header(2) + ",'workspaces':[{'name':'a','enabled':true,'accessRules':[{'kind':0,'pattern':'x','note':'','enabled':true}],'featureMaps':[]}]}");

        Assert.False(WorkspaceBundleJson.TryParse(text, out _, out var error));
        Assert.Contains("not valid JSON of the expected shape", error);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{'format':'something-else','formatVersion':2,'exportedUtc':'2026-01-01T00:00:00Z','workspaces':[]}")]
    public void JsonOfAnotherKindIsNotAWaymarkBundle(string text)
    {
        Assert.False(WorkspaceBundleJson.TryParse(Json(text), out var bundle, out var error));
        Assert.Null(bundle);
        Assert.Equal("The file is not a Waymark workspace bundle.", error);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(99)]
    public void ANewerBundleAsksForAnUpdate(int version)
    {
        Assert.False(WorkspaceBundleJson.TryParse(Json("{" + Header(version) + ",'workspaces':[]}"), out _, out var error));
        Assert.Contains("newer Waymark", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnOlderBundleThanTheOldestReadableIsRefused(int version)
    {
        Assert.False(WorkspaceBundleJson.TryParse(Json("{" + Header(version) + ",'workspaces':[]}"), out _, out var error));
        Assert.Contains("older Waymark", error);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void BothReadableVersionsAreAccepted(int version)
    {
        Assert.True(WorkspaceBundleJson.TryParse(Json("{" + Header(version) + ",'workspaces':[]}"), out var bundle, out var error), error);
        Assert.Equal(version, bundle.FormatVersion);
        Assert.Empty(bundle.Workspaces);
    }

    [Fact]
    public void AVersionOneStepReadsAsADirectCallAtDepthZero()
    {
        var text = Json("{" + Header(1) + ",'workspaces':[{'name':'a','enabled':true,'accessRules':[],'featureMaps':[{'name':'m','summary':'s','components':[{'spec':'a.cs','note':'n'}]}]}]}");

        Assert.True(WorkspaceBundleJson.TryParse(text, out var bundle, out var error), error);

        var map = Assert.Single(Assert.Single(bundle.Workspaces).FeatureMaps);
        Assert.False(map.Ordered);
        Assert.Null(map.UpdatedUtc);
        var component = Assert.Single(map.Components);
        Assert.Equal(0, component.Depth);
        Assert.Equal("call", component.Link);
    }

    [Fact]
    public void TheFormatConstantsAreStable()
    {
        Assert.Equal("waymark-workspace-bundle", WorkspaceBundleJson.FormatName);
        Assert.Equal(2, WorkspaceBundleJson.CurrentVersion);
        Assert.Equal(1, WorkspaceBundleJson.MinReadableVersion);
    }
}
