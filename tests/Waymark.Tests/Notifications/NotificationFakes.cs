using Waymark.Core.Settings;
using Waymark.Host.Notifications;

namespace Waymark.Tests.Notifications;

internal sealed class FakeNotifier : IDesktopNotifier
{
    public List<DesktopNotice> Shown { get; } = [];

    public bool IsSupported { get; set; } = true;

    /// <summary>When set, every call throws it.</summary>
    public Exception? FailWith { get; set; }

    public Task ShowAsync(DesktopNotice notice, CancellationToken ct)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        Shown.Add(notice);
        return Task.CompletedTask;
    }
}

internal sealed class FakePendingDecisions : IPendingDecisionSource
{
    public PendingDecisions Now { get; set; }

    public Task<PendingDecisions> CountAsync(CancellationToken ct) => Task.FromResult(Now);
}

internal sealed class FakeNotificationSettings : IDesktopNotificationSettings
{
    public bool Enabled { get; set; } = true;

    public Task<bool> IsEnabledAsync(CancellationToken ct) => Task.FromResult(Enabled);
}

internal sealed class Clock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A config store in memory: enough for one setting at a time.</summary>
internal sealed class InMemoryConfigStore : IConfigStore
{
    private readonly Dictionary<(SettingScope, string), SettingValue> _values = [];

    public Task<SettingValue?> GetAsync(SettingScope scope, string key, CancellationToken ct) =>
        Task.FromResult(_values.TryGetValue((scope, key), out var value) ? value : null);

    public Task<SettingValue> SetAsync(SettingScope scope, string key, string valueJson, string changedBy, CancellationToken ct)
    {
        var version = _values.TryGetValue((scope, key), out var old) ? old.Version + 1 : 1;
        var value = new SettingValue(scope, key, valueJson, version, DateTimeOffset.UnixEpoch);
        _values[(scope, key)] = value;
        return Task.FromResult(value);
    }

    public async Task<bool> TryAddAsync(SettingScope scope, string key, string valueJson, string changedBy, CancellationToken ct)
    {
        if (_values.ContainsKey((scope, key)))
        {
            return false;
        }

        await SetAsync(scope, key, valueJson, changedBy, ct);
        return true;
    }

    public Task<IReadOnlyList<SettingHistoryEntry>> GetHistoryAsync(SettingScope scope, string key, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SettingHistoryEntry>>([]);
}
