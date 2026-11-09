using Waymark.Core.Commands;

namespace Waymark.Tests.Commands;

public sealed class RiskTiersTests
{
    private static RiskTier TierOf(string key) => RiskTiers.Of(CommandCatalog.Get(key));

    [Theory]
    [InlineData("shell.run", RiskTier.RunsCode)]
    [InlineData("dotnet.build", RiskTier.RunsCode)]
    [InlineData("dotnet.test", RiskTier.RunsCode)]
    [InlineData("dotnet.format_check", RiskTier.RunsCode)]
    [InlineData("python.lint", RiskTier.RunsCode)]
    [InlineData("python.test", RiskTier.RunsCode)]
    [InlineData("node.script", RiskTier.RunsCode)]
    [InlineData("node.build", RiskTier.RunsCode)]
    public void StartingProjectOrBuildCodeRunsCode(string key, RiskTier expected) => Assert.Equal(expected, TierOf(key));

    [Theory]
    [InlineData("git.push")]
    [InlineData("git.pull")]
    [InlineData("dotnet.tool_restore")]
    [InlineData("dotnet.package_add")]
    [InlineData("python.install")]
    [InlineData("python.package_add")]
    [InlineData("python.venv")]
    [InlineData("node.install")]
    [InlineData("node.package_add")]
    public void ReachingAnotherComputerSendsOut(string key) => Assert.Equal(RiskTier.SendsOut, TierOf(key));

    [Theory]
    [InlineData("git.commit")]
    [InlineData("git.add")]
    [InlineData("git.reset_hard")]
    [InlineData("dotnet.new")]
    [InlineData("dotnet.package_remove")]
    [InlineData("proc.stop_port")]
    [InlineData("proc.clean_outputs")]
    public void ChangingFilesOrProcessesChanges(string key) => Assert.Equal(RiskTier.Change, TierOf(key));

    [Theory]
    [InlineData("git.read")]
    [InlineData("dotnet.info")]
    [InlineData("python.info")]
    [InlineData("node.info")]
    [InlineData("proc.ports")]
    [InlineData("proc.build_server")]
    public void OnlyReadingLooks(string key) => Assert.Equal(RiskTier.Look, TierOf(key));

    [Fact]
    public void AnOperationOutsideTheCatalogFollowsItsFlags()
    {
        var reads = new OperationInfo("other.read", "other", "L", "D", CommandGate.Auto, Mutating: false, DriftSensitive: false);
        var writes = reads with { Key = "other.write", Mutating = true };
        var runs = reads with { Key = "dotnet.custom", Family = CommandCatalog.DotnetFamily, DriftSensitive = true };

        Assert.Equal(RiskTier.Look, RiskTiers.Of(reads));
        Assert.Equal(RiskTier.Change, RiskTiers.Of(writes));
        Assert.Equal(RiskTier.RunsCode, RiskTiers.Of(runs));
    }

    [Fact]
    public void DriftSensitiveOperationsOutsideTheThreeLanguageFamiliesDoNotRunCode()
    {
        var git = new OperationInfo("git.custom", CommandCatalog.GitFamily, "L", "D", CommandGate.Auto, Mutating: false, DriftSensitive: true);

        Assert.Equal(RiskTier.Look, RiskTiers.Of(git));
    }

    [Fact]
    public void EveryCatalogOperationHasATier() =>
        Assert.All(CommandCatalog.Operations, o => Assert.True(Enum.IsDefined(RiskTiers.Of(o)), o.Key));

    [Fact]
    public void OfRefusesNull() => Assert.Throws<ArgumentNullException>(() => RiskTiers.Of(null!));

    [Theory]
    [InlineData("dotnet.test")]
    [InlineData("dotnet.run")]
    [InlineData("dotnet.ef_read")]
    [InlineData("dotnet.ef_migrations_add")]
    [InlineData("dotnet.ef_migrations_remove")]
    [InlineData("dotnet.ef_database_update")]
    [InlineData("python.test")]
    [InlineData("python.run")]
    [InlineData("python.typecheck")]
    [InlineData("python.list")]
    [InlineData("node.test")]
    [InlineData("node.run")]
    [InlineData("node.typecheck")]
    [InlineData("node.list")]
    [InlineData("node.build")]
    [InlineData("node.lint")]
    [InlineData("node.script")]
    [InlineData("shell.run")]
    public void OperationsThatStartTheProjectsOwnCodeAskByDefault(string key)
    {
        var operation = CommandCatalog.Get(key);

        Assert.True(RiskTiers.RunsProjectCode(operation));
        Assert.NotEqual(CommandGate.Auto, operation.Default);
    }

    [Theory]
    [InlineData("dotnet.build")]
    [InlineData("dotnet.restore")]
    [InlineData("python.lint")]
    [InlineData("node.install")]
    [InlineData("git.commit")]
    [InlineData("proc.ports")]
    public void BuildToolingAloneDoesNotRunProjectCode(string key) =>
        Assert.False(RiskTiers.RunsProjectCode(CommandCatalog.Get(key)));

    [Fact]
    public void RunsProjectCodeRefusesNull() => Assert.Throws<ArgumentNullException>(() => RiskTiers.RunsProjectCode(null!));

    [Theory]
    [InlineData(RiskTier.Look, "Reads")]
    [InlineData(RiskTier.Change, "Changes")]
    [InlineData(RiskTier.RunsCode, "Runs code")]
    [InlineData(RiskTier.SendsOut, "Sends out")]
    public void EveryTierHasALabelAndAHint(RiskTier tier, string label)
    {
        Assert.Equal(label, RiskTiers.Label(tier));
        Assert.False(string.IsNullOrWhiteSpace(RiskTiers.Hint(tier)));
    }

    [Fact]
    public void AnUnknownTierIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RiskTiers.Label((RiskTier)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => RiskTiers.Hint((RiskTier)99));
    }

    [Fact]
    public void TiersAreOrderedFromSafestToFurthestReaching()
    {
        Assert.True(RiskTier.Look < RiskTier.Change);
        Assert.True(RiskTier.Change < RiskTier.RunsCode);
        Assert.True(RiskTier.RunsCode < RiskTier.SendsOut);
    }
}
