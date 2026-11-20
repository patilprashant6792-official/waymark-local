using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Memory.Semantic;

/// <summary>One note or flow as the index embeds it: its text and the hash that says whether the stored vector still matches.</summary>
internal sealed record MemoryItem(EmbeddingItemKind Kind, long ItemId, string Text, string Hash);

/// <summary>
/// Everything of one workspace that search by meaning covers: its active notes (composed by <see cref="NoteEmbedText"/>) and its flows (composed by <see cref="FlowSemanticText"/>).
/// Names, summaries, step names, labels and notes only; never the content of a file.
/// </summary>
internal sealed class MemoryItems(INoteStore notes, IFeatureMapStore maps)
{
    private const int PageSize = 200;
    private const int MaxFlows = 2000;
    private const int GetBatch = 100;

    public async Task<IReadOnlyList<MemoryItem>> ListAsync(Workspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var items = new List<MemoryItem>();
        foreach (var note in await notes.ListActiveAsync(workspace.Id, ct).ConfigureAwait(false))
        {
            var text = NoteEmbedText.Compose(workspace.Name, note);
            items.Add(new MemoryItem(EmbeddingItemKind.Note, note.Id, text.Text, text.Hash));
        }

        foreach (var map in await ListFlowsAsync(maps, workspace.Id, ct).ConfigureAwait(false))
        {
            var text = FlowSemanticText.Compose(workspace.Id, map).Text;
            items.Add(new MemoryItem(EmbeddingItemKind.Flow, map.Id, text, NoteRules.Hash(text)));
        }

        return items;
    }

    /// <summary>Every flow of the workspace with its steps, newest first, at most 2,000.</summary>
    internal static async Task<IReadOnlyList<FeatureMap>> ListFlowsAsync(IFeatureMapStore maps, long workspaceId, CancellationToken ct)
    {
        var ids = new List<long>();
        for (var skip = 0; skip < MaxFlows; skip += PageSize)
        {
            var page = await maps.ListAsync(workspaceId, null, skip, PageSize, ct).ConfigureAwait(false);
            ids.AddRange(page.Items.Select(item => item.Id));
            if (page.Items.Count < PageSize || ids.Count >= page.Total)
            {
                break;
            }
        }

        var result = new List<FeatureMap>(ids.Count);
        foreach (var chunk in ids.Chunk(GetBatch))
        {
            result.AddRange(await maps.GetAsync(workspaceId, chunk, ct).ConfigureAwait(false));
        }

        return result;
    }
}
