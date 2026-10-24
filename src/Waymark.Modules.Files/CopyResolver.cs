using System.Globalization;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Files;

/// <summary>The patches with every copy turned into plain content, the other files that lose lines (a cut), and a description of each copy.</summary>
internal sealed record ResolvedEdit(List<PatchOperation> Patches, List<PendingFile> Cuts, List<string> Notes);

/// <summary>Turns the <c>from</c> of file_edit_lines patches into content, so the line patcher only ever sees text.</summary>
internal sealed class CopyResolver(CopySourceReader reader)
{
    public async Task<ResolvedEdit> ResolveAsync(CopySession session, Workspace workspace, GuardedPath target, IReadOnlyList<PatchOperation> patches, CancellationToken ct)
    {
        var resolved = new List<PatchOperation>(patches.Count);
        var notes = new List<string>();
        var sameFileCuts = new List<PatchOperation>();
        var otherFileCuts = new Dictionary<string, (SourceSnapshot Source, List<PatchOperation> Deletes)>(CopySession.Comparer);
        foreach (var patch in patches)
        {
            if (patch.From is null)
            {
                resolved.Add(patch);
                continue;
            }

            RequireValidFrom(patch);
            var piece = await reader.ReadAsync(session, workspace, target.RelativePath, patch.From, keepWholeFileExact: false, ct).ConfigureAwait(false);
            resolved.Add(patch with { Content = piece.Text, From = null });
            notes.Add(piece.Summary);
            if (!piece.Remove)
            {
                continue;
            }

            var delete = new PatchOperation(PatchAction.Delete, piece.StartLine, piece.EndLine);
            if (CopySession.SamePath(piece.Source.Path.AbsolutePath, target.AbsolutePath))
            {
                sameFileCuts.Add(delete);
            }
            else
            {
                if (!otherFileCuts.TryGetValue(piece.Source.Path.AbsolutePath, out var entry))
                {
                    entry = (piece.Source, []);
                    otherFileCuts[piece.Source.Path.AbsolutePath] = entry;
                }

                entry.Deletes.Add(delete);
            }
        }

        resolved.AddRange(sameFileCuts);
        return new ResolvedEdit(resolved, BuildCuts(otherFileCuts.Values, notes), notes);
    }

    private static List<PendingFile> BuildCuts(IEnumerable<(SourceSnapshot Source, List<PatchOperation> Deletes)> cuts, List<string> notes)
    {
        var pending = new List<PendingFile>();
        foreach (var (source, deletes) in cuts)
        {
            var document = LineDocument.Parse(source.Text);
            var removed = LinePatcher.Apply(document.Lines, deletes);
            var bytes = source.Format.Encode(document.ToText());
            pending.Add(new PendingFile(source.Workspace, source.Path, source.Bytes, bytes));
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"removed {removed} line(s) from {source.Workspace.Name}:{source.Path.RelativePath}"));
        }

        return pending;
    }

    private static void RequireValidFrom(PatchOperation patch)
    {
        if (patch.Content is not null)
        {
            throw new PatchValidationException("Give content or from, not both.");
        }

        if (patch.Action is not (PatchAction.Patch or PatchAction.Insert or PatchAction.Append))
        {
            throw new PatchValidationException("from works with the actions patch, insert and append.");
        }
    }
}
