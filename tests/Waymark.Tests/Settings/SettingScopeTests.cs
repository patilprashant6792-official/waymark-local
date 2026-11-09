using Waymark.Core.Settings;

namespace Waymark.Tests.Settings;

public sealed class SettingScopeTests
{
    [Fact]
    public void GlobalScopeHasNoId()
    {
        var scope = SettingScope.Global;

        Assert.Equal(SettingScopeKind.Global, scope.Kind);
        Assert.Equal(string.Empty, scope.Id);
    }

    [Theory]
    [InlineData(SettingScopeKind.Workspace)]
    [InlineData(SettingScopeKind.Project)]
    [InlineData(SettingScopeKind.Connection)]
    public void NonGlobalScopesKeepTheirId(SettingScopeKind kind)
    {
        var scope = new SettingScope(kind, "42");

        Assert.Equal(kind, scope.Kind);
        Assert.Equal("42", scope.Id);
    }

    [Fact]
    public void GlobalScopeWithAnIdIsRefused() =>
        Assert.Throws<ArgumentException>(() => new SettingScope(SettingScopeKind.Global, "1"));

    [Theory]
    [InlineData(SettingScopeKind.Workspace)]
    [InlineData(SettingScopeKind.Project)]
    [InlineData(SettingScopeKind.Connection)]
    public void NonGlobalScopeWithoutAnIdIsRefused(SettingScopeKind kind) =>
        Assert.Throws<ArgumentException>(() => new SettingScope(kind, string.Empty));

    [Fact]
    public void NullIdIsRefused() =>
        Assert.Throws<ArgumentNullException>(() => new SettingScope(SettingScopeKind.Workspace, null!));

    [Fact]
    public void ScopesCompareByKindAndId()
    {
        Assert.Equal(new SettingScope(SettingScopeKind.Workspace, "1"), new SettingScope(SettingScopeKind.Workspace, "1"));
        Assert.NotEqual(new SettingScope(SettingScopeKind.Workspace, "1"), new SettingScope(SettingScopeKind.Project, "1"));
        Assert.NotEqual(new SettingScope(SettingScopeKind.Workspace, "1"), new SettingScope(SettingScopeKind.Workspace, "2"));
        Assert.Equal(SettingScope.Global, SettingScope.Global);
    }
}
