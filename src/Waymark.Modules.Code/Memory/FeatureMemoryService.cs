using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// The flows and notes behind memory_recall and memory_save: a thin layer that hands each call to the service that does the work.
/// Reading flows is <see cref="FlowRecallService"/>; creating, replacing and deleting them is <see cref="FlowSaveService"/>;
/// reading notes is <see cref="NoteRecallService"/>; writing them is <see cref="NoteToolService"/>; the repair worklist is <see cref="Repairs.RepairRecallService"/>.
/// </summary>
internal sealed class FeatureMemoryService(
    FlowRecallService recall,
    FlowSaveService save,
    NoteRecallService noteRecall,
    NoteToolService noteTools,
    Repairs.RepairRecallService repairs) : IFeatureMemoryService
{
    public Task<string> RepairsAsync(string? workspace, int? page, int? pageSize, CancellationToken ct) =>
        repairs.RepairsAsync(workspace, page, pageSize, ct);

    public Task<string> ListAsync(string? workspace, string? query, int? page, int? pageSize, CancellationToken ct) =>
        recall.ListAsync(workspace, query, page, pageSize, ct);

    public Task<string> GetAsync(string? workspace, string? ids, int? from, CancellationToken ct) =>
        recall.GetAsync(workspace, ids, from, ct);

    public Task<string> SaveAsync(string? workspace, string? name, string? summary, string? steps, int? expectedVersion, string? newName, string? distinct, CancellationToken ct) =>
        save.SaveAsync(workspace, name, summary, steps, expectedVersion, newName, distinct, ct);

    public Task<string> DeleteAsync(string? workspace, string? name, CancellationToken ct) =>
        save.DeleteAsync(workspace, name, ct);

    public Task<string> NotesAsync(string? workspace, string? ids, string? noteIds, string? about, long? history, int? page, int? pageSize, CancellationToken ct) =>
        noteRecall.NotesAsync(workspace, ids, noteIds, about, history, page, pageSize, ct);

    public Task<string> AddNoteAsync(string? workspace, NoteAuthor author, string? flow, string? step, string? about, string? kind, string? title, string? note, CancellationToken ct) =>
        noteTools.AddAsync(workspace, author, flow, step, about, kind, title, note, ct);

    public Task<string> EditNoteAsync(string? workspace, NoteAuthor author, long? noteId, string? title, string? note, string? kind, CancellationToken ct) =>
        noteTools.EditAsync(workspace, author, noteId, title, note, kind, ct);

    public Task<string> RetireNoteAsync(string? workspace, NoteAuthor author, long? noteId, CancellationToken ct) =>
        noteTools.RetireAsync(workspace, author, noteId, ct);

    public Task<string> ConfirmNoteAsync(string? workspace, NoteAuthor author, long? noteId, CancellationToken ct) =>
        noteTools.ConfirmAsync(workspace, author, noteId, ct);
}
