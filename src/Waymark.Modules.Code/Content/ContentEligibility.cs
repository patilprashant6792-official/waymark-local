namespace Waymark.Modules.Code.Content;

/// <summary>
/// Which files code_grep leaves out even though the access policy allows them: vendored libraries and minified output.
/// Pure: no file system access, so every rule can be tested with plain strings.
/// </summary>
internal static class ContentEligibility
{
    private const string MinifiedMarker = ".min.";

    /// <summary>True for names such as 'bootstrap.min.css'.</summary>
    public static bool IsMinifiedName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return fileName.Contains(MinifiedMarker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when one of the vendored folders (a name or a '/' path) appears as consecutive folder names in the file's path.
    /// The file name itself is never compared.
    /// </summary>
    public static bool IsVendored(string relativePath, IReadOnlyList<string> vendoredFolders)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(vendoredFolders);
        if (vendoredFolders.Count == 0)
        {
            return false;
        }

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var folderCount = segments.Length - 1;
        foreach (var vendored in vendoredFolders)
        {
            var wanted = vendored.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var start = 0; start + wanted.Length <= folderCount; start++)
            {
                if (SegmentsMatch(segments, start, wanted))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// A file of at least MinifiedMinBytes whose lines average more than MinifiedAverageLineChars bytes is machine-written
    /// (minified script or style, a data blob on one line). Hand-written text rarely averages more than about 60.
    /// </summary>
    public static bool LooksMinified(long sizeBytes, int lineCount, ContentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (lineCount <= 0 || sizeBytes < options.MinifiedMinBytes)
        {
            return false;
        }

        return sizeBytes / lineCount > options.MinifiedAverageLineChars;
    }

    private static bool SegmentsMatch(string[] segments, int start, string[] wanted)
    {
        for (var i = 0; i < wanted.Length; i++)
        {
            if (!string.Equals(segments[start + i], wanted[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
