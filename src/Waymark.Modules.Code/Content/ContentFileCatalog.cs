using System.Globalization;
using Waymark.Core.Security;
using Waymark.Core.Text;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Content;

/// <summary>
/// The files code_grep would read, offered to other modules through <see cref="ITextFileCatalog"/> (file_replace uses it).
/// A glob walks the workspace with <see cref="ContentFileWalker"/>; named paths go through the path guard and the same eligibility rules one by one.
/// </summary>
internal sealed class ContentFileCatalog(IPathGuard guard, IAccessRuleCache ruleCache, FileAccessPolicy policy, ContentOptions options) : ITextFileCatalog
{
    public bool IsAvailable => true;

    public int MaxFileBytes => options.MaxFileBytes;

    public async Task<CatalogListing> ListAsync(Workspace workspace, PathGlob? glob, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        EnsureFolder(workspace);
        var rules = await ruleCache.GetAsync(workspace.Id, ct).ConfigureAwait(false);
        var walk = await Task.Run(() => ContentFileWalker.Enumerate(workspace.Path, policy, rules, options, glob, ct), ct).ConfigureAwait(false);
        var files = walk.Files.Select(file => new CatalogFile(file.RelativePath, file.AbsolutePath, file.Size)).ToList();
        return new CatalogListing(files, walk.Coverage.TooLarge, walk.Coverage.VendoredOrMinified, walk.Coverage.Unreadable, walk.Truncated, []);
    }

    public Task<CatalogListing> ListPathsAsync(Workspace workspace, IReadOnlyList<string> relativePaths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(relativePaths);
        EnsureFolder(workspace);

        var files = new List<CatalogFile>();
        var refused = new List<CatalogRefusal>();
        var seen = new HashSet<string>(Waymark.Core.Platform.PathComparisons.Comparer);
        foreach (var requested in relativePaths)
        {
            ct.ThrowIfCancellationRequested();
            var label = requested ?? string.Empty;
            GuardedPath path;
            try
            {
                path = guard.Resolve(workspace, requested);
            }
            catch (FileAccessDeniedException ex)
            {
                refused.Add(new CatalogRefusal(label, ex.Message));
                continue;
            }

            if (!seen.Add(path.RelativePath))
            {
                continue;
            }

            var reason = Check(path);
            if (reason is null)
            {
                files.Add(new CatalogFile(path.RelativePath, path.AbsolutePath, new FileInfo(path.AbsolutePath).Length));
            }
            else
            {
                refused.Add(new CatalogRefusal(path.RelativePath.Length == 0 ? label : path.RelativePath, reason));
            }
        }

        files.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(new CatalogListing(files, 0, 0, 0, false, refused));
    }

    public bool LooksMinified(long sizeBytes, int lineCount) => ContentEligibility.LooksMinified(sizeBytes, lineCount, options);

    private string? Check(GuardedPath path)
    {
        if (path.RelativePath.Length == 0 || Directory.Exists(path.AbsolutePath))
        {
            return "is a folder, not a file";
        }

        var info = new FileInfo(path.AbsolutePath);
        if (!info.Exists)
        {
            return "does not exist";
        }

        if (ContentEligibility.IsMinifiedName(info.Name) || ContentEligibility.IsVendored(path.RelativePath, options.VendoredFolders))
        {
            return "is vendored or minified";
        }

        if (info.Length == 0)
        {
            return "is empty";
        }

        return info.Length > options.MaxFileBytes
            ? string.Create(CultureInfo.InvariantCulture, $"is over {options.MaxFileBytes} bytes")
            : null;
    }

    private static void EnsureFolder(Workspace workspace)
    {
        if (!Directory.Exists(workspace.Path))
        {
            throw new InvalidOperationException($"The folder of workspace '{workspace.Name}' was not found on disk.");
        }
    }
}
