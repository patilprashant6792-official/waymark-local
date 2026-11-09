using Waymark.Core.Security;

namespace Waymark.Tests.Security;

public sealed class WorkspaceAccessRulesTests
{
    private const string SecretsReason = "This file type holds configuration secrets, keys or databases.";

    private static AccessRule Rule(AccessRuleKind kind, string pattern, bool enabled = true) =>
        new(1, 1, kind, pattern, enabled, string.Empty, DateTimeOffset.UnixEpoch);

    private static WorkspaceAccessRules Rules(params AccessRule[] rules) => new(rules);

    private static string[] Segments(string path) => path.Split('/');

    [Fact]
    public void NoRulesBlockNothing()
    {
        Assert.Null(WorkspaceAccessRules.Empty.EvaluateBlocks(Segments("src/Legacy/a.cs")));
        Assert.Empty(WorkspaceAccessRules.Empty.AllowedExtensions);
    }

    [Fact]
    public void NullArgumentsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkspaceAccessRules(null!));
        Assert.Throws<ArgumentNullException>(() => WorkspaceAccessRules.Empty.EvaluateBlocks(null!));
    }

    [Fact]
    public void NoSegmentsMeansTheWorkspaceRootAndIsNeverBlocked()
    {
        var rules = Rules(Rule(AccessRuleKind.BlockKeyword, "legacy"));

        Assert.Null(rules.EvaluateBlocks([]));
    }

    [Theory]
    [InlineData("src/Legacy/a.cs", true)]
    [InlineData("legacy/a.cs", true)]
    [InlineData("a/b/LEGACY/c/d.cs", true)]
    [InlineData("src/Legacyx/a.cs", false)]
    [InlineData("src/a.cs", false)]
    public void FolderNameRuleBlocksThatFolderAtAnyDepthIgnoringCase(string path, bool blocked)
    {
        var rules = Rules(Rule(AccessRuleKind.BlockFolder, "Legacy"));

        Assert.Equal(blocked, rules.EvaluateBlocks(Segments(path)) is not null);
    }

    [Theory]
    [InlineData("generated/a.cs", true)]
    [InlineData("x/gen/a.cs", true)]
    [InlineData("regen/a.cs", false)]
    public void FolderRuleMayUseWildcards(string path, bool blocked)
    {
        var rules = Rules(Rule(AccessRuleKind.BlockFolder, "gen*"));

        Assert.Equal(blocked, rules.EvaluateBlocks(Segments(path)) is not null);
    }

    [Theory]
    [InlineData("src/Old/a.cs", true)]
    [InlineData("SRC/old/a.cs", true)]
    [InlineData("src/Old", true)]
    [InlineData("src/Older/a.cs", false)]
    [InlineData("x/src/Old/a.cs", false)]
    [InlineData("src/a.cs", false)]
    public void FolderRuleWithASlashIsAPathFromTheWorkspaceRoot(string path, bool blocked)
    {
        var rules = Rules(Rule(AccessRuleKind.BlockFolder, "src/Old"));

        Assert.Equal(blocked, rules.EvaluateBlocks(Segments(path)) is not null);
    }

    [Theory]
    [InlineData("db/schema.sql", true)]
    [InlineData("SCHEMA.SQL", true)]
    [InlineData("db/schema.sqlx", false)]
    [InlineData("schema.sql/readme.md", false)]
    public void FileNameRuleJudgesTheLastSegmentOnly(string path, bool blocked)
    {
        var rules = Rules(Rule(AccessRuleKind.BlockFileName, "*.sql"));

        Assert.Equal(blocked, rules.EvaluateBlocks(Segments(path)) is not null);
    }

    [Theory]
    [InlineData("docs/Legacy-notes.md", true)]
    [InlineData("LEGACY/a.cs", true)]
    [InlineData("docs/notes.md", false)]
    public void KeywordRuleBlocksAnyPathContainingTheWord(string path, bool blocked)
    {
        var rules = Rules(Rule(AccessRuleKind.BlockKeyword, "legacy"));

        Assert.Equal(blocked, rules.EvaluateBlocks(Segments(path)) is not null);
    }

    [Fact]
    public void ReasonsNameTheKindAndThePattern()
    {
        const string Tail = "). Change it under Workspaces, Access rules.";

        Assert.Equal("Blocked by your workspace rule (folder 'Legacy'" + Tail, Rules(Rule(AccessRuleKind.BlockFolder, "Legacy")).EvaluateBlocks(Segments("Legacy/a.cs")));
        Assert.Equal("Blocked by your workspace rule (file name '*.sql'" + Tail, Rules(Rule(AccessRuleKind.BlockFileName, "*.sql")).EvaluateBlocks(Segments("a.sql")));
        Assert.Equal("Blocked by your workspace rule (word 'legacy'" + Tail, Rules(Rule(AccessRuleKind.BlockKeyword, "legacy")).EvaluateBlocks(Segments("a/legacy.cs")));
    }

    [Fact]
    public void FolderRulesAreCheckedBeforeFileNamesAndWords()
    {
        var rules = Rules(
            Rule(AccessRuleKind.BlockKeyword, "legacy"),
            Rule(AccessRuleKind.BlockFileName, "*.cs"),
            Rule(AccessRuleKind.BlockFolder, "legacy"));

        var reason = rules.EvaluateBlocks(Segments("legacy/a.cs"));

        Assert.NotNull(reason);
        Assert.Contains("(folder 'legacy')", reason);
    }

    [Fact]
    public void EveryRuleOfAKindIsConsidered()
    {
        var rules = Rules(Rule(AccessRuleKind.BlockFolder, "one"), Rule(AccessRuleKind.BlockFolder, "two"));

        Assert.NotNull(rules.EvaluateBlocks(Segments("one/a.cs")));
        Assert.NotNull(rules.EvaluateBlocks(Segments("two/a.cs")));
        Assert.Null(rules.EvaluateBlocks(Segments("three/a.cs")));
    }

    [Fact]
    public void DisabledRulesAreIgnored()
    {
        var rules = Rules(
            Rule(AccessRuleKind.BlockFolder, "Legacy", enabled: false),
            Rule(AccessRuleKind.AllowExtension, ".proto", enabled: false));

        Assert.Null(rules.EvaluateBlocks(Segments("Legacy/a.cs")));
        Assert.Empty(rules.AllowedExtensions);
    }

    [Fact]
    public void AllowedExtensionsAreACaseInsensitiveSetAndBlockNothing()
    {
        var rules = Rules(Rule(AccessRuleKind.AllowExtension, ".proto"));

        Assert.Contains(".PROTO", rules.AllowedExtensions);
        Assert.Null(rules.EvaluateBlocks(Segments("api/a.proto")));
    }

    [Fact]
    public void RulesAreCopiedSoLaterChangesToTheSourceListDoNotAffectThem()
    {
        var source = new List<AccessRule> { Rule(AccessRuleKind.BlockFolder, "Legacy") };
        var rules = new WorkspaceAccessRules(source);

        source.Clear();

        Assert.NotNull(rules.EvaluateBlocks(Segments("Legacy/a.cs")));
    }

    [Fact]
    public void PolicyAddsWorkspaceBlocksOnTopOfTheBuiltInOnes()
    {
        var policy = new FileAccessPolicy();
        var rules = Rules(Rule(AccessRuleKind.BlockFolder, "Legacy"));

        Assert.Contains("workspace rule", policy.Evaluate("src/Legacy/a.cs", rules));
        Assert.Null(policy.Evaluate("src/Modern/a.cs", rules));
        Assert.Contains("restricted folder", policy.Evaluate("bin/a.cs", rules));
    }

    [Fact]
    public void PolicyAcceptsAnExtraFileTypeFromTheRulesButNeverASecretOne()
    {
        var policy = new FileAccessPolicy();
        var rules = Rules(Rule(AccessRuleKind.AllowExtension, ".proto"), Rule(AccessRuleKind.AllowExtension, ".pem"));

        Assert.NotNull(policy.Evaluate("api/a.proto"));
        Assert.Null(policy.Evaluate("api/a.proto", rules));
        Assert.Equal(SecretsReason, policy.Evaluate("tls/server.pem", rules));
    }

    [Fact]
    public void PolicyKeywordRuleBlocksEvenAnAllowedFileType()
    {
        var policy = new FileAccessPolicy();
        var rules = Rules(Rule(AccessRuleKind.BlockKeyword, "draft"));

        Assert.NotNull(policy.Evaluate("docs/draft-plan.md", rules));
        Assert.Null(policy.Evaluate("docs/plan.md", rules));
    }

    [Fact]
    public void EnsureAllowedThrowsTheRuleReason()
    {
        var policy = new FileAccessPolicy();
        var rules = Rules(Rule(AccessRuleKind.BlockFileName, "*.sql"));

        var exception = Assert.Throws<FileAccessDeniedException>(() => policy.EnsureAllowed("db/a.sql", rules));

        Assert.Equal("db/a.sql", exception.Path);
        Assert.Contains("file name '*.sql'", exception.Reason);
    }
}
