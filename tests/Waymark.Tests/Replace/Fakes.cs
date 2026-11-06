using System.Text.RegularExpressions;
using Waymark.Core.Events;
using Waymark.Core.Security;
using Waymark.Core.Text;
using Waymark.Core.Workspaces;

namespace Waymark.Tests.Replace;

/// <summary>A workspace in a fresh temporary folder, deleted afterwards.</summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "waymark-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Workspace = new Workspace(1, "test", Root, true, DateTimeOffset.UtcNow, null);
    }

    public string Root { get; }

    public Workspace Workspace { get; }

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Write(string relative, byte[] bytes)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public byte[] Read(string relative) => File.ReadAllBytes(PathOf(relative));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is harmless.
        }
    }
}

/// <summary>Resolves paths inside the workspace folder; anything under 'blocked/' is refused like an access rule would.</summary>
internal sealed class FolderPathGuard(Workspace workspace) : IPathGuard
{
    public Task<Workspace> GetWorkspaceAsync(string? workspaceName, CancellationToken ct) =>
        string.Equals(workspaceName, workspace.Name, StringComparison.OrdinalIgnoreCase)
            ? Task.FromResult(workspace)
            : throw new KeyNotFoundException($"No workspace is named '{workspaceName}'.");

    public GuardedPath Resolve(Workspace target, string? relativePath)
    {
        var relative = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        if (relative.Contains("..", StringComparison.Ordinal) || relative.StartsWith("blocked/", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileAccessDeniedException(relative, "The access rules block this path.");
        }

        return new GuardedPath(target, relative, Path.GetFullPath(Path.Combine(target.Path, relative)));
    }

    public async Task<GuardedPath> ResolveAsync(string? workspaceName, string? relativePath, CancellationToken ct) =>
        Resolve(await GetWorkspaceAsync(workspaceName, ct).ConfigureAwait(false), relativePath);
}

/// <summary>Lists every file of the folder, like the code module's catalog without vendored folders.</summary>
internal sealed class FolderCatalog(IPathGuard guard) : ITextFileCatalog
{
    public bool IsAvailable => true;

    public int MaxFileBytes => 1_048_576;

    public Task<CatalogListing> ListAsync(Workspace workspace, PathGlob? glob, CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(workspace.Path, "*", SearchOption.AllDirectories)
            .Select(path => new CatalogFile(Path.GetRelativePath(workspace.Path, path).Replace('\\', '/'), path, new FileInfo(path).Length))
            .Where(file => !file.RelativePath.StartsWith("blocked/", StringComparison.OrdinalIgnoreCase))
            .Where(file => glob is null || glob.IsMatch(file.RelativePath))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(new CatalogListing(files, 0, 0, 0, false, []));
    }

    public Task<CatalogListing> ListPathsAsync(Workspace workspace, IReadOnlyList<string> relativePaths, CancellationToken ct)
    {
        var files = new List<CatalogFile>();
        var refused = new List<CatalogRefusal>();
        foreach (var relative in relativePaths)
        {
            try
            {
                var path = guard.Resolve(workspace, relative);
                if (File.Exists(path.AbsolutePath))
                {
                    files.Add(new CatalogFile(path.RelativePath, path.AbsolutePath, new FileInfo(path.AbsolutePath).Length));
                }
                else
                {
                    refused.Add(new CatalogRefusal(relative, "does not exist"));
                }
            }
            catch (FileAccessDeniedException ex)
            {
                refused.Add(new CatalogRefusal(relative, ex.Message));
            }
        }

        return Task.FromResult(new CatalogListing(files, 0, 0, 0, false, refused));
    }

    public bool LooksMinified(long sizeBytes, int lineCount) => false;
}

/// <summary>Finds AWS-style access key ids (AKIA followed by 16 capitals or digits), like the real inspector's credential shapes.</summary>
internal sealed partial class KeyInspector : IContentInspector
{
    public Task<IReadOnlyList<RedactionFinding>> InspectAsync(string text, CancellationToken ct)
    {
        var count = AccessKey().Matches(text).Count;
        IReadOnlyList<RedactionFinding> findings = count == 0 ? [] : [new RedactionFinding("aws-key", count)];
        return Task.FromResult(findings);
    }

    [GeneratedRegex("AKIA[A-Z0-9]{16}")]
    private static partial Regex AccessKey();
}

internal sealed class RecordingEventBus : IEventBus
{
    public List<object> Published { get; } = [];

    public IDisposable Subscribe<TEvent>(Func<TEvent, ValueTask> handler) => new Nothing();

    public ValueTask PublishAsync<TEvent>(TEvent evt)
    {
        Published.Add(evt!);
        return ValueTask.CompletedTask;
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}
