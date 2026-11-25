using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Waymark.Core.Calls;
using Waymark.Core.Memory;
using Waymark.Modules.Code.Memory;
using Waymark.Modules.Code.Tools;

namespace Waymark.Tests.Memory;

public sealed class MemoryToolsTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task AddNoteTakesTheAuthorFromTheCallerNeverFromArguments()
    {
        var memory = new RecordingMemory();
        var tools = new MemoryTools(memory, new FixedCaller("client-7"), NullLogger<MemoryTools>.Instance);

        await tools.Memorise("ws", "add-note", name: "flow-a", title: "One hint", note: "hint", step: "a.cs:A.B", kind: "gotcha", cancellationToken: _ct);

        var (action, author) = Assert.Single(memory.Calls);
        Assert.Equal("add", action);
        Assert.Equal(new NoteAuthor(NoteAuthorKind.Agent, "client-7"), author);
    }

    [Theory]
    [InlineData("edit_note", "edit")]
    [InlineData("retire_note", "retire")]
    [InlineData("confirm_note", "confirm")]
    public async Task NoteActionsAreRouted(string action, string expected)
    {
        var memory = new RecordingMemory();
        var tools = new MemoryTools(memory, new FixedCaller("c"), NullLogger<MemoryTools>.Instance);

        await tools.Memorise("ws", action, noteId: 4, note: "x", cancellationToken: _ct);

        Assert.Equal(expected, Assert.Single(memory.Calls).Action);
    }

    [Fact]
    public async Task RecallNotesIsRouted()
    {
        var memory = new RecordingMemory();
        var tools = new MemoryTools(memory, new FixedCaller("c"), NullLogger<MemoryTools>.Instance);

        await tools.Recall("ws", "notes", ids: "workspace", cancellationToken: _ct);

        Assert.Equal("notes", Assert.Single(memory.Calls).Action);
    }

    [Fact]
    public async Task RecallNotesByIdIsRouted()
    {
        var memory = new RecordingMemory();
        var tools = new MemoryTools(memory, new FixedCaller("c"), NullLogger<MemoryTools>.Instance);

        await tools.Recall("ws", "notes", noteIds: "41,57", cancellationToken: _ct);

        Assert.Equal("notes-by-id", Assert.Single(memory.Calls).Action);
    }

    [Fact]
    public async Task AnUnknownActionNamesTheKnownOnes()
    {
        var tools = new MemoryTools(new RecordingMemory(), new FixedCaller("c"), NullLogger<MemoryTools>.Instance);

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.Memorise("ws", "pin_note", cancellationToken: _ct));

        Assert.Contains("add_note", ex.Message, StringComparison.Ordinal);
    }

    private sealed class FixedCaller(string client) : ICallerContext
    {
        public string Client => client;

        public NoteAuthor AsModelAuthor() => new(NoteAuthorKind.Agent, client);
    }

    private sealed class RecordingMemory : IFeatureMemoryService
    {
        public List<(string Action, NoteAuthor? Author)> Calls { get; } = [];

        public Task<string> ListAsync(string? workspace, string? query, int? page, int? pageSize, CancellationToken ct) => Record("list", null);

        public Task<string> GetAsync(string? workspace, string? ids, int? from, CancellationToken ct) => Record("get", null);

        public Task<string> SaveAsync(string? workspace, string? name, string? summary, string? steps, int? expectedVersion, string? newName, string? distinct, CancellationToken ct) => Record("save", null);

        public Task<string> DeleteAsync(string? workspace, string? name, CancellationToken ct) => Record("delete", null);

        public Task<string> NotesAsync(string? workspace, string? ids, string? noteIds, string? about, long? history, int? page, int? pageSize, CancellationToken ct) => Record(noteIds is null ? "notes" : "notes-by-id", null);

        public Task<string> AddNoteAsync(string? workspace, NoteAuthor author, string? flow, string? step, string? about, string? kind, string? title, string? note, CancellationToken ct) => Record("add", author);

        public Task<string> EditNoteAsync(string? workspace, NoteAuthor author, long? noteId, string? title, string? note, string? kind, CancellationToken ct) => Record("edit", author);

        public Task<string> RetireNoteAsync(string? workspace, NoteAuthor author, long? noteId, CancellationToken ct) => Record("retire", author);

        public Task<string> ConfirmNoteAsync(string? workspace, NoteAuthor author, long? noteId, CancellationToken ct) => Record("confirm", author);

        private Task<string> Record(string action, NoteAuthor? author)
        {
            Calls.Add((action, author));
            return Task.FromResult("ok");
        }
    }
}
