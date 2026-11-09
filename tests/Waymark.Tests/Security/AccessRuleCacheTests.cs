using Microsoft.Extensions.Logging.Abstractions;
using Waymark.Core.Events;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;
using Waymark.Tests.Support;

namespace Waymark.Tests.Security;

public sealed class AccessRuleCacheTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly InMemoryAccessRuleStore _store = new();
    private readonly EventBus _events = new(NullLogger<EventBus>.Instance);
    private readonly AccessRuleCache _cache;

    public AccessRuleCacheTests() => _cache = new AccessRuleCache(_store, _events);

    public void Dispose() => _cache.Dispose();

    [Fact]
    public async Task RulesAreReadOnceAndThenServedFromMemory()
    {
        _store.Add(1, AccessRuleKind.BlockKeyword, "legacy");

        var first = await _cache.GetAsync(1, _ct);
        var second = await _cache.GetAsync(1, _ct);

        Assert.Same(first, second);
        Assert.Equal(1, _store.ListCalls);
        Assert.NotNull(first.EvaluateBlocks(["legacy", "a.cs"]));
    }

    [Fact]
    public async Task EachWorkspaceIsCachedOnItsOwn()
    {
        _store.Add(1, AccessRuleKind.BlockKeyword, "legacy");

        var one = await _cache.GetAsync(1, _ct);
        var two = await _cache.GetAsync(2, _ct);

        Assert.Equal(2, _store.ListCalls);
        Assert.NotNull(one.EvaluateBlocks(["legacy", "a.cs"]));
        Assert.Null(two.EvaluateBlocks(["legacy", "a.cs"]));
    }

    [Fact]
    public async Task InvalidateDropsOnlyThatWorkspace()
    {
        await _cache.GetAsync(1, _ct);
        await _cache.GetAsync(2, _ct);

        _cache.Invalidate(1);
        await _cache.GetAsync(1, _ct);
        await _cache.GetAsync(2, _ct);

        Assert.Equal(3, _store.ListCalls);
    }

    [Fact]
    public async Task WorkspaceChangedEventDropsTheCachedRules()
    {
        await _cache.GetAsync(1, _ct);
        _store.Add(1, AccessRuleKind.BlockKeyword, "legacy");

        await _events.PublishAsync(new WorkspaceChanged(1, "w", WorkspaceChangeKind.Removed));
        var reloaded = await _cache.GetAsync(1, _ct);

        Assert.Equal(2, _store.ListCalls);
        Assert.NotNull(reloaded.EvaluateBlocks(["legacy", "a.cs"]));
    }

    [Fact]
    public async Task ChangeOfAnotherWorkspaceKeepsTheEntry()
    {
        await _cache.GetAsync(1, _ct);

        await _events.PublishAsync(new WorkspaceChanged(2, "other", WorkspaceChangeKind.Added));
        await _cache.GetAsync(1, _ct);

        Assert.Equal(1, _store.ListCalls);
    }

    [Fact]
    public async Task RulesThatChangedWhileTheyWereBeingReadAreHandedOutButNotKept()
    {
        _store.OnList = _ =>
        {
            _cache.Invalidate(1);
            return Task.CompletedTask;
        };

        var first = await _cache.GetAsync(1, _ct);
        _store.OnList = null;
        var second = await _cache.GetAsync(1, _ct);
        var third = await _cache.GetAsync(1, _ct);

        Assert.NotNull(first);
        Assert.NotSame(first, second);
        Assert.Same(second, third);
        Assert.Equal(2, _store.ListCalls);
    }

    [Fact]
    public void SynchronousGetLoadsWhenTheWorkspaceIsNotCachedYet()
    {
        _store.Add(1, AccessRuleKind.BlockKeyword, "legacy");

        var first = _cache.Get(1);
        var second = _cache.Get(1);

        Assert.Same(first, second);
        Assert.Equal(1, _store.ListCalls);
        Assert.NotNull(first.EvaluateBlocks(["legacy", "a.cs"]));
    }

    [Fact]
    public async Task SynchronousGetUsesWhatTheAsyncGetCached()
    {
        var cached = await _cache.GetAsync(1, _ct);

        Assert.Same(cached, _cache.Get(1));
        Assert.Equal(1, _store.ListCalls);
    }

    [Fact]
    public async Task AFailedReadThrowsAndIsNotCached()
    {
        _store.FailWith = new InvalidOperationException("database is down");

        Assert.Throws<InvalidOperationException>(() => _cache.Get(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _cache.GetAsync(1, _ct));

        _store.FailWith = null;
        var recovered = await _cache.GetAsync(1, _ct);
        Assert.Null(recovered.EvaluateBlocks(["a.cs"]));
        Assert.Equal(3, _store.ListCalls);
    }

    [Fact]
    public async Task AfterDisposeEventsNoLongerDropEntries()
    {
        await _cache.GetAsync(1, _ct);

        _cache.Dispose();
        await _events.PublishAsync(new WorkspaceChanged(1, "w", WorkspaceChangeKind.Removed));
        await _cache.GetAsync(1, _ct);

        Assert.Equal(1, _store.ListCalls);
    }

    [Fact]
    public void ConstructorRefusesNulls()
    {
        Assert.Throws<ArgumentNullException>(() => new AccessRuleCache(null!, _events));
        Assert.Throws<ArgumentNullException>(() => new AccessRuleCache(_store, null!));
    }
}
