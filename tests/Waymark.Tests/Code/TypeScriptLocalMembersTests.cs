using Microsoft.Extensions.Logging.Abstractions;
using Waymark.Core.Code;
using Waymark.Core.Tools;
using Waymark.Languages.TypeScript;
using Waymark.Modules.Code.Reading;

namespace Waymark.Tests.Code;

/// <summary>
/// React components and hooks keep their logic in closures: the handlers inside a component must be members of their own (local members), the calls in
/// them must belong to them, wrapped components (forwardRef, memo) must read as functions, and <c>export default Name;</c> must make Name public.
/// </summary>
public sealed class TypeScriptLocalMembersTests : IDisposable
{
    private const string GridSource = """
        import React, { forwardRef, useCallback, useEffect, useMemo, useState } from "react";

        export interface GridRef {
          refresh: () => void;
        }

        interface GridProps {
          title: string;
        }

        const Grid = forwardRef<GridRef, GridProps>(({ title }, ref) => {
          const [rows, setRows] = useState<string[]>([]);
          const holder = React.useRef(null);

          /** Loads the rows from the server. */
          const fetchRows = useCallback(async (page: number) => {
            const response = await api.get(`/rows/${page}`);
            setRows(response.data);
          }, []);

          const retryRow = useCallback((id: string) => {
            fetchRows(1);
            toast.success(id);
          }, [fetchRows]);

          const handleRetryConfirm = useCallback(() => {
            retryRow("a");
          }, [retryRow]);

          const columns = useMemo(() => buildColumns(title), [title]);

          function describe(row: string): string {
            const inner = () => formatRow(row);
            return inner();
          }

          useEffect(() => {
            fetchRows(1);
          }, [fetchRows]);

          return <div>{rows.map(describe)}</div>;
        });

        Grid.displayName = "Grid";

        export default Grid;
        """;

    private const string CardSource = """
        import { memo, forwardRef } from "react";

        const Card = memo(forwardRef<HTMLDivElement, CardProps>((props, ref) => {
          const onClick = () => console.log(props.label);
          return <div ref={ref} onClick={onClick} />;
        }));

        const mapState = (state: State) => state.card;

        const Panel = (props: PanelProps) => <div>{props.children}</div>;

        export default connect(mapState)(Panel);
        """;

    private const string HookSource = """
        export function useThing(id: string) {
          const load = async () => fetchThing(id);
          const debounced = debounce(() => load(), 300);
          const value = 42;
          return { load, debounced, value };
        }

        export const store = configureStore({ reducer: {} });

        const lookup = new Map<string, number>();

        export const fetchUser = createAsyncThunk("user/fetch", async (id: string) => api.get(id));

        export class Service {
          run() {
            const local = () => 1;
            return local();
          }
        }
        """;

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "waymark-ts-locals-" + Guid.NewGuid().ToString("N"));

    public TypeScriptLocalMembersTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind is harmless.
        }
    }

    [Fact]
    public async Task ForwardRefComponentIsAPublicDefaultFunction()
    {
        var file = await IndexAsync("src/components/Grid.tsx", GridSource);

        var grid = Single(file, "Grid");
        Assert.Equal(CodeMemberKind.Function, grid.Kind);
        Assert.Null(grid.TypeIndex);
        Assert.Contains("public", grid.Modifiers!.Split(' '));
        Assert.Contains("default", grid.Modifiers!.Split(' '));
        Assert.StartsWith("const Grid = forwardRef<GridRef, GridProps>(", grid.Signature, StringComparison.Ordinal);
        Assert.Contains("({ title }, ref) =>", grid.Signature, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlersInsideAComponentAreLocalMembers()
    {
        var file = await IndexAsync("src/components/Grid.tsx", GridSource);

        var fetchRows = Single(file, "fetchRows");
        Assert.Equal(CodeMemberKind.Function, fetchRows.Kind);
        Assert.True(LocalMembers.IsLocal(fetchRows));
        Assert.Contains("async", fetchRows.Modifiers!.Split(' '));
        Assert.Null(fetchRows.TypeIndex);
        Assert.Equal("const fetchRows = useCallback(async (page: number) =>", fetchRows.Signature);
        Assert.Equal("Loads the rows from the server.", fetchRows.DocSummary);

        Assert.Equal(CodeMemberKind.Function, Single(file, "retryRow").Kind);
        Assert.Equal(CodeMemberKind.Function, Single(file, "handleRetryConfirm").Kind);
        Assert.Equal(CodeMemberKind.Variable, Single(file, "columns").Kind);
        Assert.Equal("function describe(row: string): string", Single(file, "describe").Signature);
        Assert.True(LocalMembers.IsLocal(Single(file, "inner")));

        // State, refs, effects and plain values stay part of the component.
        Assert.DoesNotContain(file.Members, m => m.Name is "rows" or "setRows" or "holder" or "response");
    }

    [Fact]
    public async Task OwnersFollowTheNesting()
    {
        var file = await IndexAsync("src/components/Grid.tsx", GridSource);
        var members = file.Members;
        var owners = LocalMembers.OwnerIndexes(members);

        Assert.Equal(new[] { "Grid" }, LocalMembers.OwnerChain(members, IndexOf(file, "fetchRows"), owners).ToArray());
        Assert.Equal(new[] { "Grid", "describe" }, LocalMembers.OwnerChain(members, IndexOf(file, "inner"), owners).ToArray());
        Assert.Empty(LocalMembers.OwnerChain(members, IndexOf(file, "Grid"), owners));
        Assert.True(LocalMembers.IsInside(members, IndexOf(file, "inner"), "grid", owners));
    }

    [Fact]
    public async Task CallsBelongToTheInnermostLocal()
    {
        var file = await IndexAsync("src/components/Grid.tsx", GridSource);

        Assert.Equal("handleRetryConfirm", CallerOf(file, "retryRow"));
        Assert.Equal("fetchRows", CallerOf(file, "get"));
        Assert.Equal("fetchRows", CallerOf(file, "setRows"));
        Assert.Equal("inner", CallerOf(file, "formatRow"));
        Assert.Equal("describe", CallerOf(file, "inner"));
        Assert.Equal("columns", CallerOf(file, "buildColumns"));

        // The wrapper calls and the effect are made by the component itself.
        Assert.Equal("Grid", CallerOf(file, "forwardRef"));
        Assert.Equal("Grid", CallerOf(file, "useMemo"));
        Assert.All(file.CallSites.Where(s => s.CalleeName == "useCallback"), s => Assert.Equal("Grid", s.CallerMember));
        Assert.Equal(
            new[] { "Grid", "retryRow" },
            file.CallSites.Where(s => s.CalleeName == "fetchRows").Select(s => s.CallerMember!).Order(StringComparer.Ordinal).ToArray());
        Assert.Single(file.CallSites, s => s.CalleeName == "get");
    }

    [Fact]
    public async Task LocalsGetTheirOwnBodyWords()
    {
        var file = await IndexAsync("src/components/Grid.tsx", GridSource);

        var terms = Single(file, "handleRetryConfirm").Terms;
        Assert.NotNull(terms);
        Assert.Contains("retryrow", terms!.Split(' '));
    }

    [Fact]
    public async Task MemoOfForwardRefIsUnwrappedAndDefaultExportArgumentsArePublic()
    {
        var file = await IndexAsync("src/components/Card.tsx", CardSource);

        var card = Single(file, "Card");
        Assert.Equal(CodeMemberKind.Function, card.Kind);
        Assert.StartsWith("const Card = memo(forwardRef<HTMLDivElement, CardProps>(", card.Signature, StringComparison.Ordinal);
        Assert.EndsWith("(props, ref) =>", card.Signature, StringComparison.Ordinal);
        Assert.True(LocalMembers.IsLocal(Single(file, "onClick")));

        // export default connect(mapState)(Panel): Panel is exported, mapState (an argument of the callee) is not.
        Assert.Contains("public", Single(file, "Panel").Modifiers!.Split(' '));
        Assert.Contains("default", Single(file, "Panel").Modifiers!.Split(' '));
        Assert.Equal("internal", Single(file, "mapState").Modifiers);
    }

    [Fact]
    public async Task HooksPlainValuesAndClassesKeepTheirShape()
    {
        var file = await IndexAsync("src/hooks/useThing.ts", HookSource);

        Assert.Equal("public", Single(file, "useThing").Modifiers);
        Assert.True(LocalMembers.IsLocal(Single(file, "load")));
        Assert.Equal(CodeMemberKind.Function, Single(file, "debounced").Kind);
        Assert.True(LocalMembers.IsLocal(Single(file, "debounced")));
        Assert.DoesNotContain(file.Members, m => m.Name == "value");

        var store = Single(file, "store");
        Assert.Equal(CodeMemberKind.Variable, store.Kind);
        Assert.Equal("public", store.Modifiers);
        Assert.Equal(CodeMemberKind.Variable, Single(file, "lookup").Kind);
        Assert.Equal("internal", Single(file, "lookup").Modifiers);

        var thunk = Single(file, "fetchUser");
        Assert.Equal(CodeMemberKind.Function, thunk.Kind);
        Assert.Equal("const fetchUser = createAsyncThunk(..., async (id: string) =>", thunk.Signature);

        // Locals are read for module-level functions only, not inside class methods.
        Assert.NotNull(Single(file, "run").TypeIndex);
        Assert.DoesNotContain(file.Members, m => m.Name == "local");
    }

    [Fact]
    public async Task OutlineShowsLocalsIndentedUnderAPublicComponent()
    {
        var file = await IndexAsync("src/components/Grid.tsx", GridSource);
        var output = new OutputBudget(100_000);

        OutlineRenderer.Render(output, "ws", file, includePrivate: false, truncated: false);
        var text = output.Build();

        Assert.Contains("const Grid = forwardRef<GridRef, GridProps>(", text, StringComparison.Ordinal);
        Assert.Contains("\n  const fetchRows = useCallback(async (page: number) =>", text, StringComparison.Ordinal);
        Assert.Contains("\n  function describe(row: string): string", text, StringComparison.Ordinal);
        Assert.Contains("\n    const inner = () =>", text, StringComparison.Ordinal);
        Assert.Contains("local member(s)", text, StringComparison.Ordinal);

        // A local is listed once, under its owner, not again among the module's functions.
        Assert.Equal(1, Count(text, "const fetchRows ="));
    }

    [Fact]
    public async Task MethodOnALocalNamesItsOwner()
    {
        var (file, source) = await IndexAndLoadAsync("src/components/Grid.tsx", GridSource);
        var output = new OutputBudget(100_000);

        MethodRenderer.Render(output, "ws", file, source, [Single(file, "inner")], [], [], 100_000);
        var text = output.Build();

        Assert.Contains("## `Grid › describe › inner`", text, StringComparison.Ordinal);
        Assert.Contains("| **Declared in** | `describe`", text, StringComparison.Ordinal);
        Assert.Contains("const inner = () => formatRow(row);", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MethodOnAMemberTooLargeForTheAnswerReturnsAMap()
    {
        var (file, source) = await IndexAndLoadAsync("src/components/Grid.tsx", GridSource);
        const int budget = 2_600;
        var output = new OutputBudget(budget);

        MethodRenderer.Render(output, "ws", file, source, [Single(file, "Grid")], [], [], budget);
        var text = output.Build();

        Assert.False(output.IsTruncated);
        Assert.Contains("Too large to show whole here", text, StringComparison.Ordinal);
        Assert.Contains("### Local members of `Grid`", text, StringComparison.Ordinal);
        Assert.Contains("fetchRows", text, StringComparison.Ordinal);
        Assert.Contains("\n  inner ", text, StringComparison.Ordinal);
        Assert.Contains("### Opening lines", text, StringComparison.Ordinal);
        Assert.DoesNotContain("formatRow(row)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallbackCallsThatReturnDataOrTimersAreNotMembers()
    {
        const string source = """
            export function Table(props: { rows: Row[] }) {
              const row = props.rows.find((r) => r.id === 1);
              const valid = props.rows.filter((r) => r.ok);
              const timer = setTimeout(() => console.log("x"), 10);
              const stop = store.subscribe(() => console.log("y"));
              const keep = useCallback(() => console.log("z"), []);
              return null;
            }
            """;

        var file = await IndexAsync("src/Table.tsx", source);
        var names = file.Members.Select(m => m.Name).ToArray();

        Assert.Contains("keep", names);
        Assert.DoesNotContain("row", names);
        Assert.DoesNotContain("valid", names);
        Assert.DoesNotContain("timer", names);
        Assert.DoesNotContain("stop", names);
    }

    [Fact]
    public void ScopeLinesAreTheOutermostOwnersRange()
    {
        CodeMemberEntry Entry(string name, int start, int end, bool local) =>
            new(null, CodeMemberKind.Function, name, name, null, local ? LocalMembers.Modifier : "public", null, null, start, end);

        CodeMemberEntry[] members =
        [
            Entry("Owner", 1, 20, local: false),
            Entry("outer", 3, 15, local: true),
            Entry("inner", 5, 8, local: true),
        ];

        Assert.Null(LocalMembers.ScopeLines(members, 0));
        Assert.Equal((1, 20), LocalMembers.ScopeLines(members, 1));
        Assert.Equal((1, 20), LocalMembers.ScopeLines(members, 2));
    }

    [Fact]
    public void DeclaratorsOfOneStatementDoNotOwnEachOther()
    {
        CodeMemberEntry Entry(string name, int start, int end, bool local) =>
            new(null, CodeMemberKind.Function, name, name, null, local ? LocalMembers.Modifier : "public", null, null, start, end);

        CodeMemberEntry[] members =
        [
            Entry("Owner", 1, 10, local: false),
            Entry("a", 3, 3, local: true),
            Entry("b", 3, 3, local: true),
            Entry("c", 5, 8, local: true),
            Entry("d", 6, 6, local: true),
            Entry("Next", 12, 14, local: false),
        ];

        var owners = LocalMembers.OwnerIndexes(members);

        Assert.Equal(new[] { -1, 0, 0, 0, 3, -1 }, owners);
        Assert.Equal(new[] { 1, 2, 3 }, LocalMembers.ChildrenByOwner(members, owners)[0].ToArray());
    }

    private async Task<CodeFileIndex> IndexAsync(string relativePath, string text) =>
        (await IndexAndLoadAsync(relativePath, text, load: false)).File;

    private async Task<(CodeFileIndex File, LoadedText Source)> IndexAndLoadAsync(string relativePath, string text, bool load = true)
    {
        var absolutePath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllTextAsync(absolutePath, text, _ct);

        var indexer = new TypeScriptIndexer(new CodeIndexLimits(1_000_000, 20_000, 50_000), NullLogger<TypeScriptIndexer>.Instance);
        var result = await indexer.IndexFileAsync(absolutePath, relativePath, null, _ct);
        Assert.Equal(SyntaxIndexOutcome.Indexed, result.Outcome);
        Assert.False(result.Truncated);
        var source = load ? await LoadedText.LoadAsync(absolutePath, 1_000_000, _ct) : null!;
        return (result.File!, source);
    }

    private static CodeMemberEntry Single(CodeFileIndex file, string name) => Assert.Single(file.Members, m => m.Name == name);

    private static int IndexOf(CodeFileIndex file, string name) => file.Members.ToList().FindIndex(m => m.Name == name);

    private static string? CallerOf(CodeFileIndex file, string callee) =>
        file.CallSites.First(s => s.CalleeName == callee).CallerMember;

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
