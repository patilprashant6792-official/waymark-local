using Waymark.Core.Audit;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;
using Waymark.Host.Notes;
using Waymark.Tests.Support;

namespace Waymark.Tests.Memory;

public sealed class NoteAdminServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly Workspace Ws = new(1, "app", "C:/app", true, At, null);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryChangeIsMadeAsTheOwnerAndAudited()
    {
        var notes = new RecordingNoteService();
        var audit = new FakeAuditStore();
        var service = new NoteAdminService(notes, null!, new EmptyMemorySearch(), audit, TimeProvider.System);

        await service.AddAsync(Ws, null, null, "src/A.cs", NoteKind.Why, "because", _ct);
        await service.RetireAsync(Ws, 5, _ct);
        await service.SetPinnedAsync(Ws, 5, true, _ct);
        await service.ConfirmAsync(Ws, 5, _ct);

        Assert.All(notes.Authors, author => Assert.Equal(NoteAdminService.Owner, author));
        Assert.Equal(["config.note_added", "config.note_retired", "config.note_pinned", "config.note_confirmed"], audit.Entries.Select(entry => entry.Action));
        Assert.All(audit.Entries, entry => Assert.Equal(AuditCategory.Config, entry.Category));
    }

    [Fact]
    public async Task AnImportSavesWhatPassesAndReportsTheRest()
    {
        var notes = new RecordingNoteService { RefuseBody = "bad" };
        var audit = new FakeAuditStore();
        var service = new NoteAdminService(notes, null!, new EmptyMemorySearch(), audit, TimeProvider.System);

        var result = await service.ImportAsync(Ws, [new NoteDraft("good", NoteKind.Note, null), new NoteDraft("bad", NoteKind.Note, null)], _ct);

        Assert.Equal(1, result.Saved);
        Assert.Single(result.Problems);
        Assert.Equal("config.note_imported", Assert.Single(audit.Entries).Action);
    }

    private sealed class RecordingNoteService : INoteService
    {
        public List<NoteAuthor> Authors { get; } = [];

        public string? RefuseBody { get; init; }

        public Task<NoteSaved> AddAsync(Workspace workspace, NoteAuthor author, string? flow, string? step, string? about, string? kind, string? body, CancellationToken ct)
        {
            Authors.Add(author);
            if (body == RefuseBody)
            {
                throw new ArgumentException("refused");
            }

            return Task.FromResult(new NoteSaved(Make(1, author), []));
        }

        public Task<Note> EditAsync(Workspace workspace, NoteAuthor author, long id, string? body, string? kind, CancellationToken ct) => Done(id, author);

        public Task<Note> RetireAsync(Workspace workspace, NoteAuthor author, long id, CancellationToken ct) => Done(id, author);

        public Task<Note> ConfirmAsync(Workspace workspace, NoteAuthor author, long id, CancellationToken ct) => Done(id, author);

        public Task<Note> PinAsync(Workspace workspace, NoteAuthor author, long id, bool pinned, CancellationToken ct) => Done(id, author);

        public Task<IReadOnlyDictionary<long, NoteStaleness>> CheckAsync(Workspace workspace, IReadOnlyList<Note> notes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<long, NoteStaleness>>(new Dictionary<long, NoteStaleness>());

        private Task<Note> Done(long id, NoteAuthor author)
        {
            Authors.Add(author);
            return Task.FromResult(Make(id, author));
        }

        private static Note Make(long id, NoteAuthor author) =>
            new(id, 1, null, null, null, null, null, NoteKind.Note, "b", "h", author, null, null, null, null, null, null, null, false, At, At);
    }
}
