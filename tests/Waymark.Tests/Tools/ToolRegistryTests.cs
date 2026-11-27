using Waymark.Core.Guides;
using Waymark.Core.Tools;
using Waymark.Host.Mcp;

namespace Waymark.Tests.Tools;

public sealed class ToolRegistryTests
{
    private static readonly ToolRegistry Registry = new([typeof(WaymarkTools).Assembly]);

    [Fact]
    public void TheOverviewToolIsTheOnlyHostToolAndItIsInternal()
    {
        var host = Registry.Tools.Where(t => t.Module == ToolRegistry.HostModule).ToList();

        var only = Assert.Single(host);
        Assert.Equal(GuideRules.OverviewTool, only.Name);
        Assert.True(only.Internal);
    }

    [Theory]
    [InlineData("waymark_ping")]
    [InlineData("util_datetime")]
    public void RemovedToolsAreNotRegistered(string name) =>
        Assert.False(Registry.TryGet(name, out _));

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void AnInternalToolIsOnWhateverWasSaved(bool? saved)
    {
        var tool = new ToolDescriptor("x", "host", ToolGate.Read, 1, Internal: true);

        Assert.True(ToolDefaults.IsEnabled(tool, saved));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void AnOrdinaryToolFollowsItsSavedSwitch(bool? saved, bool expected)
    {
        var tool = new ToolDescriptor("x", "code", ToolGate.Read, 1);

        Assert.Equal(expected, ToolDefaults.IsEnabled(tool, saved));
    }
}
