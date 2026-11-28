namespace Waymark.Core.Platform;

/// <summary>
/// The scratch folders Waymark's file tools write a new file's content into before moving it over the target. Each is a folder whose name starts
/// with a dot, next to the target (the same disk, so the move is atomic): the .NET SDK leaves files in such folders out of its default items
/// (**/.*/**), so dotnet watch and other watchers that follow the project's items do not see a save as two changes. Waymark's own watcher skips them.
/// </summary>
public static class ScratchPaths
{
    /// <summary>The start of every scratch folder's name.</summary>
    public const string Prefix = ".waymark-";

    /// <summary>True when a part of the relative path (folder or file) is a Waymark scratch name.</summary>
    public static bool IsScratch(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        foreach (var part in relativePath.Split('/', '\\'))
        {
            if (part.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
