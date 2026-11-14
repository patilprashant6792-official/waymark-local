using Microsoft.Extensions.Logging.Abstractions;
using Waymark.Core.Events;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;
using Waymark.Tests.Replace;
using Waymark.Tests.Support;

namespace Waymark.Tests.Security;

public sealed class PathGuardTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly TempWorkspace _temp = new();
    private readonly InMemoryAccessRuleStore _rules = new();
    private readonly InMemoryWorkspaceStore _workspaces;
    private readonly AccessRuleCache _cache;
    private readonly PathGuard _guard;

    public PathGuardTests()
    {
        _workspaces = new InMemoryWorkspaceStore(_temp.Workspace);
        _cache = new AccessRuleCache(_rules, new EventBus(NullLogger<EventBus>.Instance));
        _guard = new PathGuard(_workspaces, new FileAccessPolicy(), _cache);
    }

    private Workspace Target => _temp.Workspace;

    public void Dispose()
    {
        _cache.Dispose();
        _temp.Dispose();
    }

    private GuardedPath Resolve(string? path) => _guard.Resolve(Target, path);

    private FileAccessDeniedException Denied(string? path) => Assert.Throws<FileAccessDeniedException>(() => _guard.Resolve(Target, path));

    private string FullRoot() => Path.TrimEndingDirectorySeparator(Path.GetFullPath(_temp.Root));

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Creating links needs a privilege some machines do not give; the test then has nothing to assert.
            return false;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("./")]
    public void EmptyInputIsTheWorkspaceRoot(string? input)
    {
        var resolved = Resolve(input);

        Assert.Equal(string.Empty, resolved.RelativePath);
        Assert.Equal(FullRoot(), resolved.AbsolutePath);
    }

    [Fact]
    public void OrdinaryPathResolvesInsideTheWorkspace()
    {
        var resolved = Resolve("src/a.cs");

        Assert.Equal("src/a.cs", resolved.RelativePath);
        Assert.Equal(Path.GetFullPath(Path.Combine(_temp.Root, "src", "a.cs")), resolved.AbsolutePath);
        Assert.Same(Target, resolved.Workspace);
    }

    [Theory]
    [InlineData("src/./sub/../a.cs", "src/a.cs")]
    [InlineData("src//a.cs", "src/a.cs")]
    [InlineData("src/", "src")]
    [InlineData(" src/a.cs ", "src/a.cs")]
    [InlineData("a/b/../../c.cs", "c.cs")]
    public void PathsAreNormalisedBeforeTheyAreJudged(string input, string expected) => Assert.Equal(expected, Resolve(input).RelativePath);

    [Fact]
    public void ExistingAndMissingPathsBothResolve()
    {
        _temp.Write("present.cs", [1, 2, 3]);

        Assert.Equal("present.cs", Resolve("present.cs").RelativePath);
        Assert.Equal("missing/none.cs", Resolve("missing/none.cs").RelativePath);
    }

    [Theory]
    [InlineData("../x.cs")]
    [InlineData("src/../../x.cs")]
    [InlineData("../../../../etc/x.cs")]
    public void LeavingTheWorkspaceIsRefused(string input) =>
        Assert.Equal("The path leaves the workspace folder.", Denied(input).Reason);

    [Fact]
    public void ASiblingFolderWithTheSamePrefixIsOutside()
    {
        var sibling = Path.GetFileName(_temp.Root) + "-evil";

        Assert.Equal("The path leaves the workspace folder.", Denied("../" + sibling + "/a.cs").Reason);
    }

    [Fact]
    public void AbsolutePathIsRefused()
    {
        var denied = Denied(Path.Combine(_temp.Root, "a.cs"));

        Assert.Equal("Use a path relative to the workspace folder.", denied.Reason);
    }

    [Fact]
    public void NulCharacterIsRefused() => Assert.Equal("The path contains invalid characters.", Denied("a\0b.cs").Reason);

    [Theory]
    [InlineData("a.cs:stream")]
    [InlineData("dir/a.cs::$DATA")]
    public void AlternateDataStreamsAreRefused(string input) => Denied(input);

    [Theory]
    [InlineData("PROGRA~1/x.cs")]
    [InlineData("dir/LONGNA~1.TXT")]
    [InlineData("file~1.cs")]
    public void ShortEightDotThreeNamesAreRefusedOnWindowsOnly(string input)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("Short (8.3)", Denied(input).Reason);
        }
        else
        {
            // Short names do not exist off Windows; 'draft~1.md' is an ordinary file there.
            Assert.Equal(input, Resolve(input).RelativePath);
        }
    }

    [Fact]
    public void ATildeThatIsNotFollowedByADigitIsFine() => Assert.Equal("a~b.cs", Resolve("a~b.cs").RelativePath);

    [Theory]
    [InlineData("bin/a.cs")]
    [InlineData(".git/config")]
    [InlineData(".env")]
    [InlineData("appsettings.json")]
    [InlineData("src/Password.cs")]
    [InlineData("logo.png")]
    public void ThePolicyIsAppliedToTheResolvedPath(string input) => Denied(input);

    [Fact]
    public void PolicyIsAppliedToWhatThePathBecomesAfterNormalising() => Denied("src/../bin/a.cs");

    [Fact]
    public async Task WorkspaceRulesFromTheStoreApply()
    {
        _rules.Add(1, AccessRuleKind.BlockFolder, "Legacy");
        await _guard.GetWorkspaceAsync("test", _ct);

        var denied = Denied("src/Legacy/a.cs");

        Assert.Contains("workspace rule", denied.Reason);
        Assert.Equal("src/Modern/a.cs", Resolve("src/Modern/a.cs").RelativePath);
    }

    [Fact]
    public void ChangedRulesApplyOnlyAfterTheCacheIsInvalidated()
    {
        Assert.Equal("src/Legacy/a.cs", Resolve("src/Legacy/a.cs").RelativePath);
        _rules.Add(1, AccessRuleKind.BlockFolder, "Legacy");

        Assert.Equal("src/Legacy/a.cs", Resolve("src/Legacy/a.cs").RelativePath);
        _cache.Invalidate(1);

        Denied("src/Legacy/a.cs");
    }

    [Fact]
    public void ARuleCanOpenAnExtraFileType()
    {
        Denied("api/service.proto");

        _rules.Add(1, AccessRuleKind.AllowExtension, ".proto");
        _cache.Invalidate(1);

        Assert.Equal("api/service.proto", Resolve("api/service.proto").RelativePath);
    }

    [Fact]
    public void ResolvingWithoutAWorkspaceIsRefused() =>
        Assert.Throws<ArgumentNullException>(() => _guard.Resolve(null!, "a.cs"));

    [Fact]
    public async Task WorkspaceIsFoundByNameAndTheNameIsTrimmed()
    {
        var found = await _guard.GetWorkspaceAsync("  test  ", _ct);

        Assert.Equal(Target, found);
    }

    [Fact]
    public async Task FindingAWorkspaceLoadsItsRulesOnceSoResolveNeedsNoDatabase()
    {
        await _guard.GetWorkspaceAsync("test", _ct);
        Resolve("src/a.cs");
        Resolve("src/b.cs");

        Assert.Equal(1, _rules.ListCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankWorkspaceNameIsRefused(string? name) =>
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _guard.GetWorkspaceAsync(name, _ct));

    [Fact]
    public async Task StarIsNotAcceptedHere()
    {
        var exception = await Assert.ThrowsAnyAsync<ArgumentException>(() => _guard.GetWorkspaceAsync("*", _ct));

        Assert.Contains("one workspace at a time", exception.Message);
    }

    [Fact]
    public async Task UnknownWorkspaceIsReportedWithAHint()
    {
        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() => _guard.GetWorkspaceAsync("nope", _ct));

        Assert.Contains("'nope' not found", exception.Message);
        Assert.Contains("code_skeleton", exception.Message);
    }

    [Fact]
    public async Task DisabledWorkspaceIsRefused()
    {
        await _workspaces.SetEnabledAsync(1, false, _ct);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _guard.GetWorkspaceAsync("test", _ct));

        Assert.Contains("disabled", exception.Message);
    }

    [Fact]
    public async Task ResolveAsyncFindsTheWorkspaceThenResolves()
    {
        var resolved = await _guard.ResolveAsync("test", "src/a.cs", _ct);

        Assert.Equal("src/a.cs", resolved.RelativePath);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _guard.ResolveAsync("nope", "src/a.cs", _ct));
        await Assert.ThrowsAsync<FileAccessDeniedException>(() => _guard.ResolveAsync("test", "../x.cs", _ct));
    }

    [Fact]
    public void WorkspacePathWithATrailingSeparatorWorksTheSame()
    {
        var slashed = new Workspace(2, "slashed", _temp.Root + Path.DirectorySeparatorChar, true, DateTimeOffset.UnixEpoch, null);

        var resolved = _guard.Resolve(slashed, "src/a.cs");

        Assert.Equal("src/a.cs", resolved.RelativePath);
        Assert.Equal("The path leaves the workspace folder.", Assert.Throws<FileAccessDeniedException>(() => _guard.Resolve(slashed, "../x.cs")).Reason);
    }

    [Fact]
    public void BackslashesAreSeparatorsOnEverySystem()
    {
        // Off Windows a backslash would be part of a file name; agents write Windows separators, so they are read as '/' everywhere.
        Assert.Equal("src/a.cs", Resolve("src\\a.cs").RelativePath);
        Assert.Equal("The path leaves the workspace folder.", Denied("..\\x.cs").Reason);
        Assert.Contains("restricted folder", Denied("src\\bin\\a.cs").Reason);
    }

    [Fact]
    public void LinkThatStaysInsideTheWorkspaceIsAllowed()
    {
        Directory.CreateDirectory(_temp.PathOf("real"));
        if (!TryLink(_temp.PathOf("alias"), _temp.PathOf("real")))
        {
            return;
        }

        Assert.Equal("alias/a.cs", Resolve("alias/a.cs").RelativePath);
    }

    [Fact]
    public void LinkThatLeavesTheWorkspaceIsRefused()
    {
        var outside = Path.Combine(Path.GetTempPath(), "waymark-tests-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            if (!TryLink(_temp.PathOf("out"), outside))
            {
                return;
            }

            Assert.Contains("points outside the workspace folder", Denied("out/a.cs").Reason);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void LinkToABlockedFolderIsJudgedByThePolicyAtItsTarget()
    {
        Directory.CreateDirectory(_temp.PathOf("bin"));
        if (!TryLink(_temp.PathOf("alias"), _temp.PathOf("bin")))
        {
            return;
        }

        Assert.Contains("restricted folder", Denied("alias/a.cs").Reason);
    }
}
