using Waymark.Core.Code;

namespace Waymark.Modules.Code.Search;

/// <summary>One type of the hierarchy. Partial declarations are merged by full name and their base lists joined.</summary>
internal sealed record TypeNode(string Name, string FullName, string Kind, string RelativePath, int Line, IReadOnlyList<string> Bases);

/// <param name="Name">Simple name of the type reached.</param>
/// <param name="Node">The declaration, when the type has a base list of its own (derived types always do); null for a base type.</param>
/// <param name="Depth">1 for a direct base or derived type.</param>
/// <param name="From">Simple name of the type this step was reached from.</param>
internal sealed record HierarchyStep(string Name, TypeNode? Node, int Depth, string From);

internal sealed record HierarchyWalk(IReadOnlyList<HierarchyStep> Steps, bool Truncated)
{
    public static HierarchyWalk Empty { get; } = new([], false);
}

/// <summary>
/// The inheritance graph read from the base lists in the index. Types are matched by simple name, so two types with
/// the same name in different namespaces share their relations (the result may list a few too many, never too few).
/// </summary>
internal sealed class TypeHierarchy
{
    public const int DisplayDepth = 12;
    public const int DisplaySteps = 2_000;
    public const int MatchingDepth = 25;
    public const int MatchingSteps = 20_000;

    private readonly Dictionary<string, List<TypeNode>> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TypeNode>> _childrenOf = new(StringComparer.Ordinal);

    public TypeHierarchy(IEnumerable<CodeTypeRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var declaration in rows.GroupBy(r => r.FullName, StringComparer.Ordinal))
        {
            var first = declaration.First();
            var bases = declaration
                .SelectMany(r => BaseListParser.SimpleNames(r.BaseList))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var node = new TypeNode(first.Name, first.FullName, first.Kind, first.RelativePath, first.Line, bases);
            Add(_byName, node.Name, node);
            foreach (var baseName in bases)
            {
                Add(_childrenOf, baseName, node);
            }
        }
    }

    /// <summary>Every type that lists <paramref name="name"/> in its base list, and every type that lists one of those, and so on.</summary>
    public HierarchyWalk Descendants(string name, int maxDepth = DisplayDepth, int maxSteps = DisplaySteps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var steps = new List<HierarchyStep>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string Name, int Depth)>();
        queue.Enqueue((name, 0));
        var truncated = false;
        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (!_childrenOf.TryGetValue(current, out var children))
            {
                continue;
            }

            if (depth >= maxDepth)
            {
                truncated = true;
                continue;
            }

            foreach (var child in children)
            {
                if (!seen.Add(child.FullName))
                {
                    continue;
                }

                if (steps.Count >= maxSteps)
                {
                    return new HierarchyWalk(steps, true);
                }

                steps.Add(new HierarchyStep(child.Name, child, depth + 1, current));
                queue.Enqueue((child.Name, depth + 1));
            }
        }

        return new HierarchyWalk(steps, truncated);
    }

    /// <summary>The base types of <paramref name="name"/> and theirs, as far as the index knows them; a base type from a library ends the chain.</summary>
    public HierarchyWalk Ancestors(string name, int maxDepth = DisplayDepth, int maxSteps = DisplaySteps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var steps = new List<HierarchyStep>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { name };
        var queue = new Queue<(string Name, int Depth)>();
        queue.Enqueue((name, 0));
        var truncated = false;
        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (!_byName.TryGetValue(current, out var nodes))
            {
                continue;
            }

            if (depth >= maxDepth)
            {
                truncated = true;
                continue;
            }

            foreach (var baseName in nodes.SelectMany(n => n.Bases).Distinct(StringComparer.Ordinal))
            {
                if (!seen.Add(baseName))
                {
                    continue;
                }

                if (steps.Count >= maxSteps)
                {
                    return new HierarchyWalk(steps, true);
                }

                steps.Add(new HierarchyStep(baseName, _byName.TryGetValue(baseName, out var known) ? known[0] : null, depth + 1, current));
                queue.Enqueue((baseName, depth + 1));
            }
        }

        return new HierarchyWalk(steps, truncated);
    }

    /// <summary>
    /// The type itself, all its bases and all its derived types: the names a receiver can be declared as and still reach
    /// a member declared on this type. Null when the walk was cut short, because a partial answer would hide callers.
    /// </summary>
    public IReadOnlySet<string>? RelatedNames(string name)
    {
        var up = Ancestors(name, MatchingDepth, MatchingSteps);
        var down = Descendants(name, MatchingDepth, MatchingSteps);
        if (up.Truncated || down.Truncated)
        {
            return null;
        }

        var related = new HashSet<string>(StringComparer.Ordinal) { name };
        related.UnionWith(up.Steps.Select(s => s.Name));
        related.UnionWith(down.Steps.Select(s => s.Name));
        return related;
    }

    private static void Add(Dictionary<string, List<TypeNode>> map, string key, TypeNode node)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(node);
    }
}
