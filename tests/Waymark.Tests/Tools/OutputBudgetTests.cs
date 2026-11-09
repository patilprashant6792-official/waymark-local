using Waymark.Core.Tools;

namespace Waymark.Tests.Tools;

public sealed class OutputBudgetTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BudgetMustBePositive(int maxChars) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutputBudget(maxChars));

    [Fact]
    public void LinesAreAppendedWithALineBreakAndCounted()
    {
        var budget = new OutputBudget(100);

        Assert.True(budget.TryAppendLine("one"));
        Assert.True(budget.TryAppendLine("two"));

        Assert.Equal(8, budget.Length);
        Assert.False(budget.IsTruncated);
        Assert.StartsWith("one\ntwo\n", budget.Build());
    }

    [Fact]
    public void ALineThatFillsTheBudgetExactlyIsAccepted()
    {
        var budget = new OutputBudget(6);

        Assert.True(budget.TryAppendLine("12345"));

        Assert.False(budget.IsTruncated);
        Assert.Equal(6, budget.Length);
    }

    [Fact]
    public void ALineThatDoesNotFitIsRefusedWholeAndMarksTheOutputCut()
    {
        var budget = new OutputBudget(10);
        Assert.True(budget.TryAppendLine("12345678"));

        Assert.False(budget.TryAppendLine("abc"));

        Assert.True(budget.IsTruncated);
        Assert.Equal(9, budget.Length);
    }

    [Fact]
    public void OnceCutNothingMoreIsAddedEvenIfItWouldFit()
    {
        var budget = new OutputBudget(10);
        Assert.True(budget.TryAppendLine("12345678"));
        Assert.False(budget.TryAppendLine("too long for the rest"));

        Assert.False(budget.TryAppendLine(""));

        Assert.Equal(9, budget.Length);
    }

    [Fact]
    public void CutOutputMentionsTheLimit()
    {
        var budget = new OutputBudget(10);
        Assert.True(budget.TryAppendLine("12345678"));
        Assert.False(budget.TryAppendLine("abc"));

        var text = budget.Build();

        Assert.StartsWith("12345678\n", text);
        Assert.Contains("10", text);
        Assert.True(text.Length > 9);
    }
}
