using System.Globalization;
using Waymark.Core.Code;
using Waymark.Core.Security;
using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Search;

/// <summary>Answers "what implements or derives from this type, and what does it build on" from the base lists in the code index.</summary>
internal sealed class CodeTypesService(
    IPathGuard guard,
    ICodeSearchStore store,
    CodeSearchOptions options,
    ToolPipelineOptions pipeline) : ICodeTypesService
{
    private const int MaxMemberRows = 2_000;
    private const int MaxLookups = 400;
    private const int MaxListedDefinitions = 3;

    private enum Direction
    {
        Both,
        Derived,
        Bases,
    }

    private sealed class PrintState(string? member, Dictionary<string, CodeMemberLocation>? declared, bool includeTests)
    {
        public string? Member { get; } = member;

        public Dictionary<string, CodeMemberLocation>? Declared { get; } = declared;

        public bool IncludeTests { get; } = includeTests;

        public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);

        public int Listed { get; set; }

        public int Declaring { get; set; }

        public int SkippedTests { get; set; }
    }

    public async Task<string> TypesAsync(
        string? workspace,
        string? typeName,
        string? direction,
        string? memberName,
        bool includeTests,
        CancellationToken ct)
    {
        var name = SimpleTypeName(typeName);
        var wanted = ParseDirection(direction);
        var member = string.IsNullOrWhiteSpace(memberName) ? null : memberName.Trim();
        var searched = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);

        var definitions = (await store.FindTypesByNameAsync(searched.Id, [name], ct).ConfigureAwait(false))
            .Where(t => string.Equals(t.Name, name, StringComparison.Ordinal))
            .ToList();
        if (definitions.Count == 0)
        {
            throw new ArgumentException($"No type named '{name}' in the code index of '{searched.Name}'. Use code_search to find the exact name.");
        }

        var relations = await store.FindTypeRelationsAsync(searched.Id, options.MaxTypeRelations, ct).ConfigureAwait(false);
        var hierarchy = new TypeHierarchy(relations.Rows);
        var derived = wanted == Direction.Bases ? HierarchyWalk.Empty : hierarchy.Descendants(name);
        var bases = wanted == Direction.Derived ? HierarchyWalk.Empty : hierarchy.Ancestors(name);

        var baseNames = bases.Steps.Select(s => s.Name).Distinct(StringComparer.Ordinal).Take(MaxLookups).ToList();
        var known = baseNames.Count == 0
            ? new Dictionary<string, CodeTypeRow>(StringComparer.Ordinal)
            : (await store.FindTypesByNameAsync(searched.Id, baseNames, ct).ConfigureAwait(false))
                .GroupBy(r => r.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var declared = member is null ? null : await DeclaredAsync(searched.Id, member, ct).ConfigureAwait(false);

        var output = new OutputBudget(pipeline.MaxOutputChars);
        var state = new PrintState(member, declared, includeTests);
        WriteHeader(output, searched, definitions, derived, bases, relations.Capped);

        if (wanted != Direction.Bases)
        {
            output.TryAppendLine();
            output.TryAppendLine($"## Derived and implementing ({derived.Steps.Count})");
            output.TryAppendLine();
            if (derived.Steps.Count == 0)
            {
                output.TryAppendLine("None found.");
            }
            else
            {
                PrintDerived(output, derived.Steps.ToLookup(s => s.From, StringComparer.Ordinal), name, 1, state);
            }
        }

        if (wanted != Direction.Derived)
        {
            output.TryAppendLine();
            output.TryAppendLine($"## Bases ({bases.Steps.Count})");
            output.TryAppendLine();
            if (bases.Steps.Count == 0)
            {
                output.TryAppendLine("None found.");
            }
            else
            {
                PrintBases(output, bases.Steps.ToLookup(s => s.From, StringComparer.Ordinal), name, 1, known, state);
            }
        }

        WriteFooter(output, state, derived.Truncated || bases.Truncated);
        return output.Build().TrimEnd();
    }

    private void WriteHeader(
        OutputBudget output,
        Waymark.Core.Workspaces.Workspace searched,
        List<CodeTypeRow> definitions,
        HierarchyWalk derived,
        HierarchyWalk bases,
        bool capped)
    {
        var first = definitions[0];
        output.TryAppendLine($"# Types around `{first.Name}`");
        output.TryAppendLine();
        var where = string.Join(", ", definitions.Take(MaxListedDefinitions).Select(d => $"`{d.RelativePath}:{d.Line}`"));
        var more = definitions.Count > MaxListedDefinitions ? $" (+{definitions.Count - MaxListedDefinitions} more)" : string.Empty;
        output.TryAppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"**Workspace:** {searched.Name} | **Kind:** {first.Kind} | **Defined in:** {where}{more} | **Derived and implementing:** {derived.Steps.Count} | **Bases:** {bases.Steps.Count}"));
        output.TryAppendLine();
        output.TryAppendLine("_Read from the base lists in the syntax index and matched by simple name, so a base type from a library appears as 'not in the index'._");

        var distinct = definitions.Select(d => d.FullName).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > 1)
        {
            output.TryAppendLine($"_{distinct.Count} types are named {first.Name} ({string.Join(", ", distinct.Take(MaxListedDefinitions))}); their relations are merged._");
        }

        if (capped)
        {
            output.TryAppendLine($"_The workspace has more than {options.MaxTypeRelations} types with a base list; the hierarchy may be incomplete._");
        }

        if (searched.LastIndexedUtc is null)
        {
            output.TryAppendLine($"_Workspace '{searched.Name}' has not finished its first index; results may be incomplete._");
        }
    }

    private static void WriteFooter(OutputBudget output, PrintState state, bool truncated)
    {
        if (state.Member is not null)
        {
            output.TryAppendLine();
            output.TryAppendLine($"_`{state.Member}` is declared on {state.Declaring} of the {state.Listed} types listed._");
        }

        if (state.SkippedTests > 0)
        {
            output.TryAppendLine();
            output.TryAppendLine($"_{state.SkippedTests} derived types in test folders are not listed (includeTests=true shows them)._");
        }

        if (truncated)
        {
            output.TryAppendLine();
            output.TryAppendLine($"_The walk stopped at {TypeHierarchy.DisplaySteps} types or depth {TypeHierarchy.DisplayDepth}; the hierarchy goes further._");
        }
    }

    private static bool PrintDerived(OutputBudget output, ILookup<string, HierarchyStep> byParent, string parent, int depth, PrintState state)
    {
        foreach (var step in byParent[parent]
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ThenBy(s => s.Node!.RelativePath, StringComparer.Ordinal))
        {
            var node = step.Node!;
            if (!state.IncludeTests && TestPaths.IsTest(node.RelativePath))
            {
                state.SkippedTests++;
                continue;
            }

            if (!state.Seen.Add(node.FullName))
            {
                continue;
            }

            var declares = Declares(state, node.FullName);
            if (!output.TryAppendLine($"{Indent(depth)}- `{node.Name}` {node.Kind} `{node.RelativePath}:{node.Line}`{declares}"))
            {
                return false;
            }

            state.Listed++;
            if (declares.Length > 0)
            {
                state.Declaring++;
            }

            if (!PrintDerived(output, byParent, node.Name, depth + 1, state))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PrintBases(
        OutputBudget output,
        ILookup<string, HierarchyStep> byParent,
        string parent,
        int depth,
        Dictionary<string, CodeTypeRow> known,
        PrintState state)
    {
        foreach (var step in byParent[parent].OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            string line;
            var declares = string.Empty;
            if (known.TryGetValue(step.Name, out var row))
            {
                declares = Declares(state, row.FullName);
                line = $"{Indent(depth)}- `{row.Name}` {row.Kind} `{row.RelativePath}:{row.Line}`{declares}";
            }
            else
            {
                line = $"{Indent(depth)}- `{step.Name}` (not in the index)";
            }

            if (!output.TryAppendLine(line))
            {
                return false;
            }

            state.Listed++;
            if (declares.Length > 0)
            {
                state.Declaring++;
            }

            if (!PrintBases(output, byParent, step.Name, depth + 1, known, state))
            {
                return false;
            }
        }

        return true;
    }

    private static string Declares(PrintState state, string fullName) =>
        state.Declared is not null && state.Declared.TryGetValue(fullName, out var location)
            ? $"  [declares {state.Member} :{location.Line}]"
            : string.Empty;

    private static string Indent(int depth) => new(' ', (depth - 1) * 2);

    private async Task<Dictionary<string, CodeMemberLocation>> DeclaredAsync(long workspaceId, string member, CancellationToken ct)
    {
        var locations = await store.FindMembersByNameAsync(workspaceId, member, MaxMemberRows, ct).ConfigureAwait(false);
        return locations
            .Where(l => l.OwnerFullName is not null)
            .GroupBy(l => l.OwnerFullName!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    private string SimpleTypeName(string? typeName)
    {
        var name = (typeName ?? string.Empty).Trim();
        var generic = name.IndexOf('<', StringComparison.Ordinal);
        if (generic >= 0)
        {
            name = name[..generic];
        }

        name = name[(name.LastIndexOf('.') + 1)..].Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("typeName is required.");
        }

        return name.Length > options.MaxQueryLength
            ? throw new ArgumentException($"typeName is longer than {options.MaxQueryLength} characters.")
            : name;
    }

    private static Direction ParseDirection(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction))
        {
            return Direction.Both;
        }

        return Enum.TryParse<Direction>(direction.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new ArgumentException($"Invalid direction '{direction}'. Valid values: {string.Join(", ", Enum.GetNames<Direction>())}.");
    }
}
