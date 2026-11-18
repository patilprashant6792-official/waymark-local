using Microsoft.Extensions.Logging.Abstractions;
using Waymark.Core.Events;
using Waymark.Core.Guides;
using Waymark.Core.Workspaces;
using Waymark.Host.Mcp;

namespace Waymark.Tests.Mcp;

public sealed class GuideDeliveriesTests : IDisposable
{
    private readonly EventBus _events = new(NullLogger<EventBus>.Instance);
    private readonly FakeGuideStore _store = new();
    private readonly GuideDeliveries _deliveries;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public GuideDeliveriesTests() => _deliveries = new GuideDeliveries(_store, _events, TimeProvider.System);

    public void Dispose() => _deliveries.Dispose();

    [Fact]
    public async Task AWorkspaceGuideIsTrackedLikeAnyModule()
    {
        var module = WorkspaceGuideRules.ModuleKey(5);

        Assert.Equal(GuideNotice.NeverLoaded, await _deliveries.CheckAsync("c", module, "h1", _ct));
        Assert.Equal(GuideNotice.None, await _deliveries.CheckAsync("c", module, "h1", _ct));

        await _deliveries.RecordAsync("c", module, "h1", _ct);
        Assert.True(await _deliveries.HoldsAsync("c", module, "h1", _ct));
        Assert.Equal(GuideNotice.Changed, await _deliveries.CheckAsync("c", module, "h2", _ct));
    }

    [Fact]
    public async Task ARemovedWorkspaceIsForgottenForEveryClient()
    {
        var module = WorkspaceGuideRules.ModuleKey(5);
        await _deliveries.RecordAsync("a", module, "h", _ct);
        await _deliveries.RecordAsync("b", module, "h", _ct);
        await _deliveries.RecordAsync("a", "code", "h", _ct);

        await _events.PublishAsync(new WorkspaceChanged(5, "api", WorkspaceChangeKind.Removed));

        Assert.False(await _deliveries.HoldsAsync("a", module, "h", _ct));
        Assert.False(await _deliveries.HoldsAsync("b", module, "h", _ct));
        Assert.True(await _deliveries.HoldsAsync("a", "code", "h", _ct));
        Assert.Equal(GuideNotice.NeverLoaded, await _deliveries.CheckAsync("a", module, "h", _ct));
    }

    private sealed class FakeGuideStore : IGuideStore
    {
        public Task<IReadOnlyList<GuideOverride>> ListOverridesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<GuideOverride>>([]);

        public Task SetOverrideAsync(string module, string content, string changedBy, DateTimeOffset now, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> DeleteOverrideAsync(string module, CancellationToken ct) => Task.FromResult(false);

        public Task<IReadOnlyList<GuideDelivery>> ListDeliveriesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<GuideDelivery>>([]);

        public Task RecordDeliveryAsync(string clientId, string module, string contentHash, DateTimeOffset now, CancellationToken ct) => Task.CompletedTask;
    }
}
