namespace Waymark.Core.Code;

/// <summary>
/// Members declared inside the body of another member outside any type: a handler inside a React component, a helper inside a custom hook,
/// a function inside a function. An indexer marks them with the <see cref="Modifier"/> modifier and keeps them outside any type
/// (<see cref="CodeMemberEntry.TypeIndex"/> is null); their owner is the closest member whose lines enclose theirs. The index stores no owner
/// column, so readers work it out here from the line ranges, which syntax keeps properly nested.
/// </summary>
public static class LocalMembers
{
    /// <summary>The modifier word that marks a local member.</summary>
    public const string Modifier = "local";

    public static bool IsLocal(CodeMemberEntry member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.Modifiers is not null && member.Modifiers.Split(' ').Contains(Modifier, StringComparer.Ordinal);
    }

    /// <summary>
    /// For each member, the index of the member that owns it, or -1 when it is not local or no enclosing member was found.
    /// One sweep over the members sorted by start line, so it stays linear enough for files with thousands of members.
    /// </summary>
    public static int[] OwnerIndexes(IReadOnlyList<CodeMemberEntry> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var owners = new int[members.Count];
        Array.Fill(owners, -1);
        if (!members.Any(IsLocal))
        {
            return owners;
        }

        // Only members outside any type can own a local one: locals are produced for module-level functions only.
        var order = Enumerable.Range(0, members.Count)
            .Where(i => members[i].TypeIndex is null)
            .OrderBy(i => members[i].StartLine)
            .ThenByDescending(i => members[i].EndLine)
            .ThenBy(i => i)
            .ToList();
        var open = new List<int>();
        foreach (var index in order)
        {
            var member = members[index];
            while (open.Count > 0 && members[open[^1]].EndLine < member.StartLine)
            {
                open.RemoveAt(open.Count - 1);
            }

            if (IsLocal(member))
            {
                // Two declarators of one statement share its lines; neither owns the other, so look below them.
                for (var j = open.Count - 1; j >= 0; j--)
                {
                    var candidate = members[open[j]];
                    if (Encloses(candidate, member))
                    {
                        owners[index] = open[j];
                        break;
                    }
                }
            }

            open.Add(index);
        }

        return owners;
    }

    /// <summary>The direct local members of each owner, ordered by start line. Keyed by the owner's index.</summary>
    public static Dictionary<int, List<int>> ChildrenByOwner(IReadOnlyList<CodeMemberEntry> members, int[]? owners = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        owners ??= OwnerIndexes(members);
        var children = new Dictionary<int, List<int>>();
        for (var i = 0; i < owners.Length; i++)
        {
            if (owners[i] < 0)
            {
                continue;
            }

            if (!children.TryGetValue(owners[i], out var list))
            {
                list = [];
                children[owners[i]] = list;
            }

            list.Add(i);
        }

        foreach (var list in children.Values)
        {
            list.Sort((a, b) => members[a].StartLine != members[b].StartLine ? members[a].StartLine.CompareTo(members[b].StartLine) : a.CompareTo(b));
        }

        return children;
    }

    /// <summary>The names of the members that enclose the member at <paramref name="index"/>, outermost first; empty for a member that is not local.</summary>
    public static IReadOnlyList<string> OwnerChain(IReadOnlyList<CodeMemberEntry> members, int index, int[]? owners = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        owners ??= OwnerIndexes(members);
        var chain = new List<string>();
        var current = owners[index];
        var guard = 0;
        while (current >= 0 && guard++ < members.Count)
        {
            chain.Add(members[current].Name);
            current = owners[current];
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>True when the member at <paramref name="index"/> is declared, at any depth, inside a member named <paramref name="ownerName"/> (ignoring case).</summary>
    public static bool IsInside(IReadOnlyList<CodeMemberEntry> members, int index, string ownerName, int[]? owners = null) =>
        OwnerChain(members, index, owners).Contains(ownerName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The line range of the outermost member that declares the member at <paramref name="index"/> (the component or function it lives in), or null when the member is not local.
    /// A local member can only be called from inside that range.
    /// </summary>
    public static (int StartLine, int EndLine)? ScopeLines(IReadOnlyList<CodeMemberEntry> members, int index, int[]? owners = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        owners ??= OwnerIndexes(members);
        var current = owners[index];
        if (current < 0)
        {
            return null;
        }

        var guard = 0;
        while (owners[current] >= 0 && guard++ < members.Count)
        {
            current = owners[current];
        }

        return (members[current].StartLine, members[current].EndLine);
    }

    private static bool Encloses(CodeMemberEntry outer, CodeMemberEntry inner) =>
        outer.StartLine <= inner.StartLine
        && outer.EndLine >= inner.EndLine
        && (outer.StartLine != inner.StartLine || outer.EndLine != inner.EndLine);
}
