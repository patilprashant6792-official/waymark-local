using Waymark.Core.Commands;

namespace Waymark.Tests.Commands;

public sealed class CommandCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CommandGateSetting Setting(long workspaceId, string operation, CommandGate gate) => new(workspaceId, operation, gate, Now);

    [Fact]
    public void EveryOperationKeyIsUniqueAndDotted()
    {
        var keys = CommandCatalog.Operations.Select(o => o.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.Contains('.', key));
    }

    [Fact]
    public void EveryOperationBelongsToAKnownFamilyAndHasATextForTheOwner()
    {
        string[] families =
        [
            CommandCatalog.DotnetFamily, CommandCatalog.PythonFamily, CommandCatalog.NodeFamily,
            CommandCatalog.GitFamily, CommandCatalog.ProcessFamily, CommandCatalog.ShellFamily,
        ];

        Assert.All(CommandCatalog.Operations, o =>
        {
            Assert.Contains(o.Family, families);
            Assert.False(string.IsNullOrWhiteSpace(o.Label), o.Key);
            Assert.False(string.IsNullOrWhiteSpace(o.Description), o.Key);
        });
    }

    [Fact]
    public void EveryFamilyHasAtLeastOneOperation()
    {
        var present = CommandCatalog.Operations.Select(o => o.Family).ToHashSet();

        Assert.Equal(6, present.Count);
    }

    [Fact]
    public void NoDefaultIsAboveTheCapOfItsOperation() =>
        Assert.All(CommandCatalog.Operations, o => Assert.True(o.Default <= o.MaxGate, o.Key));

    [Fact]
    public void OnlyTheShellAndAnyPackageScriptAreCappedAtAsk()
    {
        var capped = CommandCatalog.Operations.Where(o => o.MaxGate == CommandGate.Ask).Select(o => o.Key).Order().ToList();

        Assert.Equal(["node.script", "shell.run"], capped);
    }

    [Theory]
    [InlineData("git.reset_hard")]
    [InlineData("git.clean")]
    [InlineData("git.branch_delete")]
    public void DestructiveGitOperationsStartSwitchedOff(string key) =>
        Assert.Equal(CommandGate.Off, CommandCatalog.Get(key).Default);

    [Theory]
    [InlineData("git.read")]
    [InlineData("dotnet.info")]
    [InlineData("python.info")]
    [InlineData("node.info")]
    [InlineData("proc.ports")]
    public void ReadOnlyOperationsRunAtOnce(string key)
    {
        var operation = CommandCatalog.Get(key);

        Assert.Equal(CommandGate.Auto, operation.Default);
        Assert.False(operation.Mutating);
    }

    [Fact]
    public void TheShellOperationIsAlwaysApprovedByTheOwner()
    {
        var shell = CommandCatalog.Get(CommandCatalog.ShellRun);

        Assert.Equal(CommandCatalog.ShellFamily, shell.Family);
        Assert.Equal(CommandGate.Ask, shell.Default);
        Assert.Equal(CommandGate.Ask, shell.MaxGate);
        Assert.True(shell.Mutating);
    }

    [Fact]
    public void GetFindsAnOperationByItsKey()
    {
        var operation = CommandCatalog.Get("dotnet.build");

        Assert.Equal("Build", operation.Label);
        Assert.Equal(CommandCatalog.DotnetFamily, operation.Family);
        Assert.True(CommandCatalog.TryGet("dotnet.build", out var found));
        Assert.Same(operation, found);
    }

    [Fact]
    public void KeysAreCaseSensitive()
    {
        Assert.False(CommandCatalog.TryGet("DOTNET.BUILD", out var found));
        Assert.Null(found);
        Assert.Throws<KeyNotFoundException>(() => CommandCatalog.Get("DOTNET.BUILD"));
    }

    [Fact]
    public void AnUnknownKeyIsNamedInTheError()
    {
        var exception = Assert.Throws<KeyNotFoundException>(() => CommandCatalog.Get("nope.nothing"));

        Assert.Contains("nope.nothing", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void GetRefusesABlankKey(string key) => Assert.Throws<ArgumentException>(() => CommandCatalog.Get(key));

    [Fact]
    public void NullKeysAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => CommandCatalog.Get(null!));
        Assert.Throws<ArgumentNullException>(() => CommandCatalog.TryGet(null!, out _));
    }

    [Fact]
    public void WithoutAnySettingTheDefaultApplies()
    {
        var gate = CommandCatalog.Resolve(CommandCatalog.Get("dotnet.test"), 7, []);

        Assert.Equal(CommandGate.Ask, gate);
    }

    [Fact]
    public void ASettingForAllWorkspacesOverridesTheDefault()
    {
        var settings = new[] { Setting(CommandCatalog.AllWorkspaces, "dotnet.test", CommandGate.Auto) };

        Assert.Equal(CommandGate.Auto, CommandCatalog.Resolve(CommandCatalog.Get("dotnet.test"), 7, settings));
        Assert.Equal(CommandGate.Auto, CommandCatalog.Resolve(CommandCatalog.Get("dotnet.test"), null, settings));
    }

    [Fact]
    public void TheWorkspacesOwnSettingBeatsTheSettingForAllWorkspaces()
    {
        var settings = new[]
        {
            Setting(CommandCatalog.AllWorkspaces, "dotnet.test", CommandGate.Auto),
            Setting(7, "dotnet.test", CommandGate.Off),
        };

        Assert.Equal(CommandGate.Off, CommandCatalog.Resolve(CommandCatalog.Get("dotnet.test"), 7, settings));
        Assert.Equal(CommandGate.Auto, CommandCatalog.Resolve(CommandCatalog.Get("dotnet.test"), 8, settings));
    }

    [Fact]
    public void ASettingOfAnotherWorkspaceOrAnotherOperationIsIgnored()
    {
        var settings = new[]
        {
            Setting(9, "dotnet.test", CommandGate.Off),
            Setting(7, "dotnet.build", CommandGate.Off),
        };

        Assert.Equal(CommandGate.Ask, CommandCatalog.Resolve(CommandCatalog.Get("dotnet.test"), 7, settings));
    }

    [Fact]
    public void AWorkspaceSettingIsIgnoredWhenNoWorkspaceIsGiven()
    {
        var settings = new[] { Setting(7, "dotnet.test", CommandGate.Auto) };

        Assert.Equal(CommandGate.Ask, CommandCatalog.Resolve(CommandCatalog.Get("dotnet.test"), null, settings));
    }

    [Theory]
    [InlineData("shell.run")]
    [InlineData("node.script")]
    public void ASettingCannotRaiseAnOperationAboveItsCap(string key)
    {
        var settings = new[] { Setting(CommandCatalog.AllWorkspaces, key, CommandGate.Auto) };

        Assert.Equal(CommandGate.Ask, CommandCatalog.Resolve(CommandCatalog.Get(key), 1, settings));
    }

    [Fact]
    public void ASettingMayLowerAnOperationBelowItsDefault()
    {
        var settings = new[] { Setting(CommandCatalog.AllWorkspaces, "git.read", CommandGate.Off) };

        Assert.Equal(CommandGate.Off, CommandCatalog.Resolve(CommandCatalog.Get("git.read"), 1, settings));
    }

    [Fact]
    public void ResolveRefusesNulls()
    {
        Assert.Throws<ArgumentNullException>(() => CommandCatalog.Resolve(null!, 1, []));
        Assert.Throws<ArgumentNullException>(() => CommandCatalog.Resolve(CommandCatalog.Get("git.read"), 1, null!));
    }

    [Theory]
    [InlineData(CommandRunStatus.WaitingApproval, true)]
    [InlineData(CommandRunStatus.Queued, true)]
    [InlineData(CommandRunStatus.Running, true)]
    [InlineData(CommandRunStatus.Succeeded, false)]
    [InlineData(CommandRunStatus.Failed, false)]
    [InlineData(CommandRunStatus.Denied, false)]
    [InlineData(CommandRunStatus.TimedOut, false)]
    [InlineData(CommandRunStatus.Cancelled, false)]
    public void OnlyWaitingQueuedAndRunningRunsCanStillChange(CommandRunStatus status, bool expected) =>
        Assert.Equal(expected, status.IsActive());
}
