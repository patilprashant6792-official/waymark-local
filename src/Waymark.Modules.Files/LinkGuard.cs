using Waymark.Core.Security;

namespace Waymark.Modules.Files;

internal static class LinkGuard
{
    /// <summary>
    /// PathGuard lets a link stay inside the workspace; changing files through one would still alter something
    /// other than the named path, so the file tools refuse every existing link on the way.
    /// </summary>
    /// <exception cref="FileAccessDeniedException">An existing folder or file on the path is a link.</exception>
    public static void EnsureNoLinks(string workspaceRoot, string relativePath)
    {
        var current = Path.GetFullPath(workspaceRoot);
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists)
            {
                return;
            }

            if (info.LinkTarget is not null)
            {
                throw new FileAccessDeniedException(relativePath, "A link on the path was found; Waymark does not change files through links.");
            }
        }
    }
}
