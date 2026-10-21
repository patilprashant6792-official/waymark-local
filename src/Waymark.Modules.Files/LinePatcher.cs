namespace Waymark.Modules.Files;

/// <summary>A set of patches is invalid; nothing was changed.</summary>
internal sealed class PatchValidationException(string message) : Exception(message);

/// <summary>Validates a set of line patches against the original line count, then applies them bottom-up.</summary>
internal static class LinePatcher
{
    /// <summary>Applies all patches to <paramref name="lines"/> or none. Returns the number of lines affected.</summary>
    /// <exception cref="PatchValidationException">A patch is out of range, incomplete, or overlaps another.</exception>
    public static int Apply(List<string> lines, IReadOnlyList<PatchOperation> patches)
    {
        var total = lines.Count;
        foreach (var patch in patches)
        {
            Validate(patch, total);
        }

        var ranged = patches.Where(p => p.Action != PatchAction.Append).Select(p => new Slot(p, StartOf(p), EndOf(p))).OrderBy(s => s.Start).ToList();
        for (var i = 0; i < ranged.Count - 1; i++)
        {
            if (ranged[i].End >= ranged[i + 1].Start)
            {
                throw new PatchValidationException($"Patches overlap: {Describe(ranged[i].Patch)} conflicts with {Describe(ranged[i + 1].Patch)}.");
            }
        }

        var affected = 0;
        foreach (var slot in Enumerable.Reverse(ranged))
        {
            affected += ApplyRanged(lines, slot.Patch);
        }

        foreach (var append in patches.Where(p => p.Action == PatchAction.Append))
        {
            var added = LineDocument.SplitContent(append.Content!);
            lines.AddRange(added);
            affected += added.Count;
        }

        return affected;
    }

    private sealed record Slot(PatchOperation Patch, int Start, int End);

    // An insert after line N sits on the point N+1, so it conflicts with any patch or delete that covers line N+1.
    private static int StartOf(PatchOperation p) => p.Action == PatchAction.Insert ? p.StartLine + 1 : p.StartLine;

    private static int EndOf(PatchOperation p) => p.Action == PatchAction.Insert ? p.StartLine + 1 : p.EndLine;

    private static void Validate(PatchOperation p, int total)
    {
        switch (p.Action)
        {
            case null:
                throw new PatchValidationException("Every patch needs an action: patch, insert, delete or append.");
            case PatchAction.Patch or PatchAction.Delete:
                if (p.StartLine < 1 || p.StartLine > total)
                {
                    throw new PatchValidationException($"{p.Action}: startLine {p.StartLine} is out of range (the file has {total} lines).");
                }

                if (p.EndLine < p.StartLine)
                {
                    throw new PatchValidationException($"{p.Action}: endLine {p.EndLine} must be at least startLine {p.StartLine}.");
                }

                if (p.EndLine > total)
                {
                    throw new PatchValidationException($"{p.Action}: endLine {p.EndLine} is out of range (the file has {total} lines).");
                }

                RequireContent(p, p.Action == PatchAction.Patch);
                break;
            case PatchAction.Insert:
                if (p.StartLine < 0 || p.StartLine > total)
                {
                    throw new PatchValidationException($"Insert: startLine {p.StartLine} is out of range (0 to {total}; 0 prepends).");
                }

                RequireContent(p, true);
                break;
            case PatchAction.Append:
                RequireContent(p, true);
                break;
            default:
                throw new PatchValidationException($"Unknown action {p.Action}.");
        }
    }

    private static void RequireContent(PatchOperation p, bool required)
    {
        if (required && p.Content is null)
        {
            throw new PatchValidationException($"{p.Action} needs content.");
        }
    }

    private static int ApplyRanged(List<string> lines, PatchOperation p)
    {
        switch (p.Action)
        {
            case PatchAction.Patch:
                var removed = p.EndLine - p.StartLine + 1;
                lines.RemoveRange(p.StartLine - 1, removed);
                lines.InsertRange(p.StartLine - 1, LineDocument.SplitContent(p.Content!));
                return removed;
            case PatchAction.Insert:
                var inserted = LineDocument.SplitContent(p.Content!);
                lines.InsertRange(p.StartLine, inserted);
                return inserted.Count;
            default:
                var deleted = p.EndLine - p.StartLine + 1;
                lines.RemoveRange(p.StartLine - 1, deleted);
                return deleted;
        }
    }

    private static string Describe(PatchOperation p) =>
        p.Action == PatchAction.Insert ? $"insert after line {p.StartLine}" : $"{p.Action} lines {p.StartLine}-{p.EndLine}".ToLowerInvariant();
}
