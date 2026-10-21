namespace Waymark.Modules.Code.Search;

internal static class TestPaths
{
    /// <summary>A folder named Test or Tests, or a project folder ending in .Test or .Tests. The file name itself is not judged.</summary>
    public static bool IsTest(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var segments = relativePath.Split('/');
        return segments.Take(segments.Length - 1).Any(segment =>
            segment.Equals("test", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("tests", StringComparison.OrdinalIgnoreCase)
            || segment.EndsWith(".test", StringComparison.OrdinalIgnoreCase)
            || segment.EndsWith(".tests", StringComparison.OrdinalIgnoreCase));
    }
}
