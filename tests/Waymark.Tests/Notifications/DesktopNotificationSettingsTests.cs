using Waymark.Core.Settings;
using Waymark.Host.Notifications;
using Waymark.Tests.Support;

namespace Waymark.Tests.Notifications;

public sealed class DesktopNotificationSettingsTests
{
    private readonly InMemoryConfigStore _config = new();
    private readonly FakeAuditStore _audit = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly DesktopNotificationSettings _settings;

    public DesktopNotificationSettingsTests()
    {
        _settings = new DesktopNotificationSettings(_config, _audit, new Clock(DateTimeOffset.UnixEpoch), new CapturingLogger<DesktopNotificationSettings>());
    }

    [Fact]
    public async Task OnByDefault()
    {
        Assert.True(await _settings.IsEnabledAsync(_ct));
    }

    [Fact]
    public async Task SwitchingOffIsStoredAndAuditedOnce()
    {
        await _settings.SetEnabledAsync(false, _ct);
        await _settings.SetEnabledAsync(false, _ct);

        Assert.False(await _settings.IsEnabledAsync(_ct));
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("config.desktop_notifications", entry.Action);
        Assert.Equal("off", entry.ArgsRedacted);
    }

    [Fact]
    public async Task AValueThatCannotBeReadCountsAsOn()
    {
        await _config.SetAsync(SettingScope.Global, SettingKeys.DesktopNotifications, "\"maybe\"", "test", _ct);

        Assert.True(await _settings.IsEnabledAsync(_ct));
    }
}
