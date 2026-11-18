using Microsoft.Extensions.Logging.Abstractions;
using Waymark.Core.Events;
using Waymark.Core.Guides;
using Waymark.Core.Workspaces;

namespace Waymark.Tests.Guides;

public sealed class WorkspaceGuidesTests : IDisposable
{
    private readonly FakeStore _store = new();
    private readonly EventBus _events = new(NullLogger<EventBus>.Instance);
    private readonly WorkspaceGuides _guides;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public WorkspaceGuidesTests() => _guides = new WorkspaceGuides(_store, _events, TimeProvider.System);

    public void Dispose() => _guides.Dispose();

    [Fact]
    public async Task AWorkspaceWithoutTextIsEmpty()
    {
        var text = await _guides.GetAsync(7, _ct);

        Assert.True(text.IsEmpty);
        Assert.False(text.HasInstructions);
        Assert.Null(text.ChangedUtc);
    }

    [Fact]
    public async Task SavedTextIsCleanedStoredAndKept()
    {
        await _guides.SaveAsync(7, "  .NET   API\n", "\r\nRun the tests.\r\n", "admin", _ct);

        var text = await _guides.GetAsync(7, _ct);
        Assert.Equal(".NET API", text.Summary);
        Assert.Equal("Run the tests.", text.Instructions);
        Assert.Equal("admin", text.ChangedBy);
        Assert.True(text.HasInstructions);
        Assert.Equal((".NET API", "Run the tests."), (_store.Rows[7].Summary, _store.Rows[7].Instructions));
    }

    [Fact]
    public async Task SavingBothEmptyRemovesTheText()
    {
        await _guides.SaveAsync(7, "s", "i", "admin", _ct);
        await _guides.SaveAsync(7, " ", "\n", "admin", _ct);

        Assert.True((await _guides.GetAsync(7, _ct)).IsEmpty);
        Assert.False(_store.Rows.ContainsKey(7));
        Assert.Empty(await _guides.ListAsync(_ct));
    }

    [Fact]
    public async Task TextOverALimitIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _guides.SaveAsync(7, new string('a', WorkspaceGuideRules.MaxSummaryChars + 1), string.Empty, "admin", _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _guides.SaveAsync(7, string.Empty, new string('a', WorkspaceGuideRules.MaxInstructionsChars + 1), "admin", _ct));
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task TextWithASecretIsRefused()
    {
        var token = "ghp_" + new string('A', 36);

        await Assert.ThrowsAsync<ArgumentException>(() => _guides.SaveAsync(7, string.Empty, "use " + token, "admin", _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _guides.SaveAsync(7, "token " + token, string.Empty, "admin", _ct));
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task AWorkspaceThatNoLongerExistsIsReported()
    {
        _store.Missing.Add(9);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _guides.SaveAsync(9, "s", "i", "admin", _ct));
        Assert.True((await _guides.GetAsync(9, _ct)).IsEmpty);
    }

    [Fact]
    public async Task ARemovedWorkspaceIsForgotten()
    {
        await _guides.SaveAsync(7, "s", "i", "admin", _ct);

        await _events.PublishAsync(new WorkspaceChanged(7, "api", WorkspaceChangeKind.Removed));

        Assert.True((await _guides.GetAsync(7, _ct)).IsEmpty);
    }

    [Fact]
    public async Task ADisabledWorkspaceKeepsItsText()
    {
        await _guides.SaveAsync(7, "s", "i", "admin", _ct);

        await _events.PublishAsync(new WorkspaceChanged(7, "api", WorkspaceChangeKind.Disabled));

        Assert.True((await _guides.GetAsync(7, _ct)).HasInstructions);
    }

    [Fact]
    public async Task StoredTextIsReadOnce()
    {
        _store.Rows[3] = new WorkspaceGuideRow(3, "s", "i", DateTimeOffset.UnixEpoch, "admin");

        Assert.Equal("i", (await _guides.GetAsync(3, _ct)).Instructions);
        Assert.Equal("i", (await _guides.GetAsync(3, _ct)).Instructions);
        Assert.Equal(1, _store.ListCalls);
    }

    [Fact]
    public async Task SavingAndClearingAnnounceTheChangeAndARefusalDoesNot()
    {
        var seen = new List<long>();
        using var subscription = _events.Subscribe<WorkspaceGuideChanged>(changed =>
        {
            seen.Add(changed.WorkspaceId);
            return ValueTask.CompletedTask;
        });

        await _guides.SaveAsync(7, "s", "i", "admin", _ct);
        await _guides.SaveAsync(7, string.Empty, string.Empty, "admin", _ct);
        await Assert.ThrowsAsync<ArgumentException>(() => _guides.SaveAsync(7, new string('a', WorkspaceGuideRules.MaxSummaryChars + 1), string.Empty, "admin", _ct));

        Assert.Equal([7L, 7L], seen);
    }
    private sealed class FakeStore : IWorkspaceGuideStore
    {
        public Dictionary<long, WorkspaceGuideRow> Rows { get; } = [];

        public HashSet<long> Missing { get; } = [];

        public int ListCalls { get; private set; }

        public Task<IReadOnlyList<WorkspaceGuideRow>> ListAsync(CancellationToken ct)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<WorkspaceGuideRow>>([.. Rows.Values]);
        }

        public Task<bool> UpsertAsync(long workspaceId, string summary, string instructions, string changedBy, DateTimeOffset now, CancellationToken ct)
        {
            if (Missing.Contains(workspaceId))
            {
                return Task.FromResult(false);
            }

            Rows[workspaceId] = new WorkspaceGuideRow(workspaceId, summary, instructions, now, changedBy);
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(long workspaceId, CancellationToken ct) => Task.FromResult(Rows.Remove(workspaceId));
    }
}
