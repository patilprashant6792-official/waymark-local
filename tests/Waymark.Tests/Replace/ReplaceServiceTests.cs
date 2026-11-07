using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Waymark.Core.Events;
using Waymark.Modules.Code.Rewriting;
using Waymark.Modules.Files;
using Waymark.Modules.Files.Replace;

namespace Waymark.Tests.Replace;

public sealed class ReplaceServiceTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly TempWorkspace _workspace = new();
    private readonly RecordingEventBus _events = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _workspace.Dispose();

    private ReplaceService CreateService(ReplaceOptions? options = null)
    {
        var limits = options ?? ReplaceOptions.Defaults;
        var guard = new FolderPathGuard(_workspace.Workspace);
        var inspector = new KeyInspector();
        var filesOptions = new FilesOptions(2_000_000, 50, 200, 20_000, 10_000_000, 50, 500);
        return new ReplaceService(
            guard,
            new ReplacePlanner(new FolderCatalog(guard), limits, new CSharpRewriter()),
            new ReplacePlanStore(limits, TimeProvider.System),
            new ReplaceApplier(guard, inspector, _events, filesOptions, NullLogger<ReplaceApplier>.Instance),
            new FileWriteGate(),
            inspector,
            limits,
            NullLogger<ReplaceService>.Instance);
    }

    private static ReplaceRequest Glob(string find, string with, string glob = "**", ReplaceMode mode = ReplaceMode.Literal, bool caseSensitive = true, bool wholeWord = false) =>
        new("test", find, with, glob, null, mode, caseSensitive, wholeWord);

    private void WriteText(string relative, string text) => _workspace.Write(relative, Utf8.GetBytes(text));

    private string ReadText(string relative) => Utf8.GetString(_workspace.Read(relative));

    [Fact]
    public async Task ApplyChangesOnlyTheMatchedCharacters()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        _workspace.Write("a.txt", [.. bom, .. Utf8.GetBytes("a Foo\r\nb Foo\nc\r\n")]);
        var service = CreateService();

        var preview = await service.PreviewAsync(Glob("Foo", "Bar"), _ct);
        Assert.Equal(2, preview.MatchCount);
        Assert.NotNull(preview.PlanId);
        Assert.Equal(new[] { 1, 2 }, preview.Files.Single().Lines.Select(line => line.Number));

        var result = await service.ApplyAsync(Glob("Foo", "Bar"), preview.PlanId!, 2, [], _ct);

        Assert.True(result.Applied, result.Refusal);
        Assert.Equal(bom.Concat(Utf8.GetBytes("a Bar\r\nb Bar\nc\r\n")).ToArray(), _workspace.Read("a.txt"));
        Assert.Single(_events.Published.OfType<FileChanged>());
    }

    [Fact]
    public async Task ApplyIsAllFilesAcrossTheScope()
    {
        WriteText("src/one.cs", "Invoice\n");
        WriteText("src/two.cs", "x Invoice y Invoice\n");
        WriteText("docs/readme.md", "Invoice\n");
        var service = CreateService();

        var preview = await service.PreviewAsync(Glob("Invoice", "Order", "src/**"), _ct);
        var result = await service.ApplyAsync(Glob("Invoice", "Order", "src/**"), preview.PlanId!, 3, [], _ct);

        Assert.True(result.Applied, result.Refusal);
        Assert.Equal("Order\n", ReadText("src/one.cs"));
        Assert.Equal("x Order y Order\n", ReadText("src/two.cs"));
        Assert.Equal("Invoice\n", ReadText("docs/readme.md"));
    }

    [Fact]
    public async Task FileChangedAfterPreviewRefusesEverything()
    {
        WriteText("a.txt", "Foo\n");
        WriteText("b.txt", "Foo\n");
        var service = CreateService();
        var preview = await service.PreviewAsync(Glob("Foo", "Bar"), _ct);

        WriteText("b.txt", "Foo edited\n");
        var result = await service.ApplyAsync(Glob("Foo", "Bar"), preview.PlanId!, 2, [], _ct);

        Assert.False(result.Applied);
        Assert.Contains("b.txt", result.Refusal, StringComparison.Ordinal);
        Assert.Equal("Foo\n", ReadText("a.txt"));
    }

    [Fact]
    public async Task DeletedFileRefusesEverything()
    {
        WriteText("a.txt", "Foo\n");
        WriteText("b.txt", "Foo\n");
        var service = CreateService();
        var preview = await service.PreviewAsync(Glob("Foo", "Bar"), _ct);

        File.Delete(_workspace.PathOf("b.txt"));
        var result = await service.ApplyAsync(Glob("Foo", "Bar"), preview.PlanId!, 2, [], _ct);

        Assert.False(result.Applied);
        Assert.Contains("deleted", result.Refusal, StringComparison.Ordinal);
        Assert.Equal("Foo\n", ReadText("a.txt"));
    }

    [Fact]
    public async Task WrongCountIsRefusedAndThePlanStaysUsable()
    {
        WriteText("a.txt", "Foo Foo\n");
        var service = CreateService();
        var preview = await service.PreviewAsync(Glob("Foo", "Bar"), _ct);

        var wrong = await service.ApplyAsync(Glob("Foo", "Bar"), preview.PlanId!, 3, [], _ct);
        Assert.False(wrong.Applied);
        Assert.Equal("Foo Foo\n", ReadText("a.txt"));

        var right = await service.ApplyAsync(Glob("Foo", "Bar"), preview.PlanId!, 2, [], _ct);
        Assert.True(right.Applied, right.Refusal);
    }

    [Fact]
    public async Task OtherArgumentsAreRefused()
    {
        WriteText("a.txt", "Foo\n");
        var service = CreateService();
        var preview = await service.PreviewAsync(Glob("Foo", "Bar"), _ct);

        var result = await service.ApplyAsync(Glob("Foo", "Baz"), preview.PlanId!, 1, [], _ct);

        Assert.False(result.Applied);
        Assert.Equal("Foo\n", ReadText("a.txt"));
    }

    [Fact]
    public async Task PlanIsUsedOnce()
    {
        WriteText("a.txt", "Foo\n");
        var service = CreateService();
        var preview = await service.PreviewAsync(Glob("Foo", "FooBar"), _ct);

        Assert.True((await service.ApplyAsync(Glob("Foo", "FooBar"), preview.PlanId!, 1, [], _ct)).Applied);
        var second = await service.ApplyAsync(Glob("Foo", "FooBar"), preview.PlanId!, 1, [], _ct);

        Assert.False(second.Applied);
        Assert.Equal("FooBar\n", ReadText("a.txt"));
    }

    [Fact]
    public async Task ExclusionsLeaveLinesAndFilesAlone()
    {
        WriteText("a.txt", "Foo\nFoo\n");
        WriteText("b.txt", "Foo\n");
        var service = CreateService();
        var preview = await service.PreviewAsync(Glob("Foo", "Bar"), _ct);

        var result = await service.ApplyAsync(Glob("Foo", "Bar"), preview.PlanId!, 1, ["a.txt#L2", "b.txt"], _ct);

        Assert.True(result.Applied, result.Refusal);
        Assert.Equal(2, result.Excluded);
        Assert.Equal("Bar\nFoo\n", ReadText("a.txt"));
        Assert.Equal("Foo\n", ReadText("b.txt"));
    }

    [Fact]
    public async Task SecretInWithIsRefused()
    {
        WriteText("a.txt", "key = PLACEHOLDER\n");
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewAsync(Glob("PLACEHOLDER", "AKIAABCDEFGHIJKLMNOP"), _ct));
    }

    [Fact]
    public async Task ReplacementThatCompletesASecretIsRefused()
    {
        WriteText("a.txt", "AKIA-ABCDEFGHIJKLMNOP\n");
        var service = CreateService();
        var preview = await service.PreviewAsync(Glob("-", string.Empty), _ct);

        var result = await service.ApplyAsync(Glob("-", string.Empty), preview.PlanId!, 1, [], _ct);

        Assert.False(result.Applied);
        Assert.Equal("AKIA-ABCDEFGHIJKLMNOP\n", ReadText("a.txt"));
    }

    [Fact]
    public async Task SecretAlreadyOnTheLineDoesNotBlock()
    {
        WriteText("a.txt", "AKIAABCDEFGHIJKLMNOP name=Foo\n");
        var service = CreateService();
        var preview = await service.PreviewAsync(Glob("Foo", "Bar"), _ct);

        var result = await service.ApplyAsync(Glob("Foo", "Bar"), preview.PlanId!, 1, [], _ct);

        Assert.True(result.Applied, result.Refusal);
        Assert.Equal("AKIAABCDEFGHIJKLMNOP name=Bar\n", ReadText("a.txt"));
    }

    [Fact]
    public async Task CSharpModeRenamesIdentifiersOnly()
    {
        WriteText("src/Foo.cs", "class Foo\n{\n    string s = \"Foo\"; // Foo\n    Foo Make() => new Foo();\n}\n");
        WriteText("docs/Foo.md", "Foo\n");
        var service = CreateService();
        var request = Glob("Foo", "Bar", mode: ReplaceMode.CSharp);

        var preview = await service.PreviewAsync(request, _ct);
        Assert.Equal(3, preview.MatchCount);
        Assert.Equal(1, preview.Summary.NotCSharp);

        var result = await service.ApplyAsync(request, preview.PlanId!, 3, [], _ct);

        Assert.True(result.Applied, result.Refusal);
        Assert.Equal("class Bar\n{\n    string s = \"Foo\"; // Foo\n    Bar Make() => new Bar();\n}\n", ReadText("src/Foo.cs"));
        Assert.Equal("Foo\n", ReadText("docs/Foo.md"));
    }

    [Fact]
    public async Task CSharpModeRefusesAKeyword()
    {
        var service = CreateService();
        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewAsync(Glob("Foo", "class", mode: ReplaceMode.CSharp), _ct));
    }

    [Fact]
    public async Task NothingMatchedGivesNoPlan()
    {
        WriteText("a.txt", "nothing here\n");
        var preview = await CreateService().PreviewAsync(Glob("Foo", "Bar"), _ct);

        Assert.Null(preview.PlanId);
        Assert.NotEmpty(preview.Blockers);
    }

    [Fact]
    public async Task BlockedNamedPathGivesNoPlan()
    {
        WriteText("a.txt", "Foo\n");
        WriteText("blocked/b.txt", "Foo\n");
        var preview = await CreateService().PreviewAsync(new ReplaceRequest("test", "Foo", "Bar", null, ["a.txt", "blocked/b.txt"]), _ct);

        Assert.Null(preview.PlanId);
        Assert.Single(preview.Refused);
    }

    [Fact]
    public async Task OverTheFileLimitGivesNoPlan()
    {
        WriteText("a.txt", "Foo\n");
        WriteText("b.txt", "Foo\n");
        var preview = await CreateService(ReplaceOptions.Defaults with { MaxFiles = 1 }).PreviewAsync(Glob("Foo", "Bar"), _ct);

        Assert.Null(preview.PlanId);
        Assert.Equal(2, preview.MatchCount);
    }

    [Fact]
    public async Task BinaryFileIsLeftOut()
    {
        _workspace.Write("data.bin", [0x46, 0x6F, 0x6F, 0x00, 0x01]);
        WriteText("a.txt", "Foo\n");
        var preview = await CreateService().PreviewAsync(Glob("Foo", "Bar"), _ct);

        Assert.Equal(1, preview.MatchCount);
        Assert.Equal(1, preview.Summary.NotText);
    }

    [Fact]
    public async Task WholeWordAndCaseApply()
    {
        WriteText("a.txt", "order Order OrderId\n");
        var service = CreateService();
        var request = Glob("order", "item", caseSensitive: false, wholeWord: true);
        var preview = await service.PreviewAsync(request, _ct);

        var result = await service.ApplyAsync(request, preview.PlanId!, 2, [], _ct);

        Assert.True(result.Applied, result.Refusal);
        Assert.Equal("item item OrderId\n", ReadText("a.txt"));
    }
}
