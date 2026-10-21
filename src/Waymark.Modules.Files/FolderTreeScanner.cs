namespace Waymark.Modules.Files;

internal static class FolderTreeScanner
{
    private static readonly EnumerationOptions AllEntries = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Null when the whole tree can be deleted safely, otherwise the reason it cannot.
    /// A link inside the tree is refused so a delete can never reach what the link points to, and the entry count is bounded.
    /// </summary>
    public static string? FindDeleteBlocker(string absoluteFolder, int maxEntries)
    {
        var count = 0;
        foreach (var entry in new DirectoryInfo(absoluteFolder).EnumerateFileSystemInfos("*", AllEntries))
        {
            count++;
            if (count > maxEntries)
            {
                return $"The folder holds more than {maxEntries} entries; delete its subfolders one at a time.";
            }

            if (entry.LinkTarget is not null)
            {
                return "The folder contains a link; Waymark does not delete through links.";
            }
        }

        return null;
    }
}
