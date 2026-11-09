using Waymark.Core.Security;

namespace Waymark.Tests.Security;

public sealed class AccessRuleRulesTests
{
    [Theory]
    [InlineData(AccessRuleKind.BlockFolder, "Legacy", "Legacy")]
    [InlineData(AccessRuleKind.BlockFolder, "src\\Legacy", "src/Legacy")]
    [InlineData(AccessRuleKind.BlockFolder, " src / Legacy ", "src/Legacy")]
    [InlineData(AccessRuleKind.BlockFolder, "gen*", "gen*")]
    [InlineData(AccessRuleKind.BlockFolder, "/src//old/", "src/old")]
    [InlineData(AccessRuleKind.BlockFolder, "my folder", "my folder")]
    [InlineData(AccessRuleKind.BlockFileName, "*.sql", "*.sql")]
    [InlineData(AccessRuleKind.BlockFileName, "notes-*.txt", "notes-*.txt")]
    [InlineData(AccessRuleKind.BlockFileName, "  Secrets?.json  ", "Secrets?.json")]
    [InlineData(AccessRuleKind.BlockKeyword, "Legacy", "legacy")]
    [InlineData(AccessRuleKind.BlockKeyword, "my-word_1.x", "my-word_1.x")]
    [InlineData(AccessRuleKind.BlockKeyword, "ABC", "abc")]
    [InlineData(AccessRuleKind.AllowExtension, "proto", ".proto")]
    [InlineData(AccessRuleKind.AllowExtension, ".PROTO", ".proto")]
    [InlineData(AccessRuleKind.AllowExtension, "Gql", ".gql")]
    [InlineData(AccessRuleKind.AllowExtension, ".abcdefghijklmno", ".abcdefghijklmno")]
    public void ValidPatternIsAcceptedAndNormalised(AccessRuleKind kind, string pattern, string expected)
    {
        var accepted = AccessRuleRules.TryNormalize(kind, pattern, out var normalized, out var error);

        Assert.True(accepted, error);
        Assert.Equal(expected, normalized);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData(AccessRuleKind.BlockFolder, "", "Enter a pattern.")]
    [InlineData(AccessRuleKind.BlockKeyword, "   ", "Enter a pattern.")]
    [InlineData(AccessRuleKind.BlockFolder, "/", "Enter a folder name.")]
    [InlineData(AccessRuleKind.BlockFolder, "..", "not allowed")]
    [InlineData(AccessRuleKind.BlockFolder, "src/./x", "not allowed")]
    [InlineData(AccessRuleKind.BlockFolder, "src/*", "Wildcards work in a single folder name")]
    [InlineData(AccessRuleKind.BlockFolder, "a/b?c", "Wildcards work in a single folder name")]
    [InlineData(AccessRuleKind.BlockFolder, "bad!", "Use letters, digits")]
    [InlineData(AccessRuleKind.BlockFolder, "***", "at least one letter or digit")]
    [InlineData(AccessRuleKind.BlockFileName, "a/b.cs", "cannot contain")]
    [InlineData(AccessRuleKind.BlockFileName, "a\\b.cs", "cannot contain")]
    [InlineData(AccessRuleKind.BlockFileName, "a$b", "cannot contain")]
    [InlineData(AccessRuleKind.BlockFileName, "*.*", "at least one letter or digit")]
    [InlineData(AccessRuleKind.BlockFileName, "*", "at least one letter or digit")]
    [InlineData(AccessRuleKind.BlockKeyword, "ab", "at least 3 characters")]
    [InlineData(AccessRuleKind.BlockKeyword, "my word", "Use letters, digits")]
    [InlineData(AccessRuleKind.BlockKeyword, "a/b", "Use letters, digits")]
    [InlineData(AccessRuleKind.BlockKeyword, "*abc", "Use letters, digits")]
    [InlineData(AccessRuleKind.AllowExtension, ".", "dot followed by")]
    [InlineData(AccessRuleKind.AllowExtension, "pro-to", "dot followed by")]
    [InlineData(AccessRuleKind.AllowExtension, "a b", "dot followed by")]
    [InlineData(AccessRuleKind.AllowExtension, ".abcdefghijklmnop", "dot followed by")]
    [InlineData((AccessRuleKind)99, "x", "Unknown rule type.")]
    public void InvalidPatternIsRefusedWithAReason(AccessRuleKind kind, string pattern, string fragment)
    {
        var accepted = AccessRuleRules.TryNormalize(kind, pattern, out var normalized, out var error);

        Assert.False(accepted);
        Assert.Equal(string.Empty, normalized);
        Assert.Contains(fragment, error);
    }

    [Theory]
    [InlineData(AccessRuleKind.BlockFolder)]
    [InlineData(AccessRuleKind.BlockFileName)]
    [InlineData(AccessRuleKind.BlockKeyword)]
    [InlineData(AccessRuleKind.AllowExtension)]
    public void MissingPatternIsRefused(AccessRuleKind kind)
    {
        var accepted = AccessRuleRules.TryNormalize(kind, null, out var normalized, out var error);

        Assert.False(accepted);
        Assert.Equal(string.Empty, normalized);
        Assert.Equal("Enter a pattern.", error);
    }

    [Fact]
    public void PatternLengthLimitIsInclusive()
    {
        var longest = new string('a', AccessRuleRules.MaxPatternLength);
        var tooLong = new string('a', AccessRuleRules.MaxPatternLength + 1);

        Assert.True(AccessRuleRules.TryNormalize(AccessRuleKind.BlockFileName, longest, out _, out _));
        Assert.False(AccessRuleRules.TryNormalize(AccessRuleKind.BlockFileName, tooLong, out _, out var error));
        Assert.Equal("Use at most 100 characters.", error);
    }

    [Fact]
    public void LimitsAreTheDocumentedOnes()
    {
        Assert.Equal(100, AccessRuleRules.MaxPatternLength);
        Assert.Equal(200, AccessRuleRules.MaxNoteLength);
        Assert.Equal(200, AccessRuleRules.MaxRulesPerWorkspace);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("A short note", true)]
    [InlineData("line\nbreak", false)]
    [InlineData("tab\there", false)]
    public void NoteMustBeShortPlainText(string? note, bool expected) => Assert.Equal(expected, AccessRuleRules.IsValidNote(note));

    [Fact]
    public void NoteLengthLimitIsInclusive()
    {
        Assert.True(AccessRuleRules.IsValidNote(new string('x', AccessRuleRules.MaxNoteLength)));
        Assert.False(AccessRuleRules.IsValidNote(new string('x', AccessRuleRules.MaxNoteLength + 1)));
    }
}
