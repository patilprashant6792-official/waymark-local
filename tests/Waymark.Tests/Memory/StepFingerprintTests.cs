using Waymark.Modules.Code.Memory;

namespace Waymark.Tests.Memory;

public sealed class StepFingerprintTests
{
    [Fact]
    public void TrailingWhiteSpaceDoesNotCount() =>
        Assert.Equal(
            StepFingerprint.Compute(["void A()  ", "{ }"], [new LineSpan(1, 2)]),
            StepFingerprint.Compute(["void A()", "{ }\t"], [new LineSpan(1, 2)]));

    [Fact]
    public void AChangedLineChangesTheFingerprint() =>
        Assert.NotEqual(
            StepFingerprint.Compute(["void A()", "{ return 1; }"], [new LineSpan(1, 2)]),
            StepFingerprint.Compute(["void A()", "{ return 2; }"], [new LineSpan(1, 2)]));

    [Fact]
    public void LinesOutsideTheRangesDoNotCount() =>
        Assert.Equal(
            StepFingerprint.Compute(["x", "void A()", "y"], [new LineSpan(2, 2)]),
            StepFingerprint.Compute(["changed", "void A()", "changed too"], [new LineSpan(2, 2)]));

    [Fact]
    public void OverloadsAreTakenInFileOrder() =>
        Assert.Equal(
            StepFingerprint.Compute(["a", "b", "c"], [new LineSpan(3, 3), new LineSpan(1, 1)]),
            StepFingerprint.Compute(["a", "b", "c"], [new LineSpan(1, 1), new LineSpan(3, 3)]));

    [Fact]
    public void RangesAreClampedToTheFile() =>
        Assert.Equal(
            StepFingerprint.Compute(["a"], [new LineSpan(1, 1)]),
            StepFingerprint.Compute(["a"], [new LineSpan(0, 9)]));

    [Fact]
    public void TheFingerprintIsVersioned() =>
        Assert.StartsWith(StepFingerprint.Prefix, StepFingerprint.Compute(["a"], [new LineSpan(1, 1)]), StringComparison.Ordinal);
}
