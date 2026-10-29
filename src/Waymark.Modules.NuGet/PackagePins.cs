using Waymark.Core.NuGetIndex;
using Waymark.Core.Security;
using Waymark.Core.Text;

namespace Waymark.Modules.NuGet;

/// <summary>Looks up the version a project pins for a package, so a lookup shows the API the project really runs.</summary>
public interface IPackagePins
{
    /// <summary>
    /// The version the lock file of the project pins for <paramref name="packageId"/> in the given ecosystem (pypi: uv.lock, poetry.lock, pdm.lock, pylock.toml;
    /// npm: package-lock.json, pnpm-lock.yaml, yarn.lock); null when no workspace was named (the caller then falls back to the latest).
    /// </summary>
    /// <exception cref="ArgumentException">The workspace or the project folder is unknown, no lock file exists, or the package is not in it.</exception>
    Task<string?> FindAsync(string ecosystem, string? workspace, string? project, string packageId, CancellationToken ct);
}

internal sealed class PackagePins(IPathGuard guard) : IPackagePins
{
    public async Task<string?> FindAsync(string ecosystem, string? workspace, string? project, string packageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return null;
        }

        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var root = Path.GetFullPath(found.Path);
        var folder = root;
        if (!string.IsNullOrWhiteSpace(project))
        {
            var target = guard.Resolve(found, project.Trim()).AbsolutePath;
            folder = Directory.Exists(target) ? target : Path.GetDirectoryName(target) ?? root;
        }

        var where = string.IsNullOrWhiteSpace(project) ? "the workspace" : $"'{project}'";
        if (ecosystem == PackageEcosystems.Npm)
        {
            var pins = await NodeLockFiles.ReadAsync(root, folder, ct).ConfigureAwait(false)
                ?? throw new ArgumentException($"No lock file (package-lock.json, pnpm-lock.yaml or yarn.lock) was found for {where}. Pass 'version' yourself, as code_skeleton shows it.");
            return pins.Versions.TryGetValue(packageId, out var npmVersion)
                ? npmVersion
                : throw new ArgumentException($"'{packageId}' is not in {pins.LockFile}, so this project does not install it. Pass 'version' to look at a release anyway.");
        }

        var python = await PythonLockFiles.ReadAsync(root, folder, ct).ConfigureAwait(false)
            ?? throw new ArgumentException($"No lock file (uv.lock, poetry.lock, pdm.lock or pylock.toml) was found for {where}. Pass 'version' yourself, as code_skeleton shows it.");
        return python.Versions.TryGetValue(PythonLockFiles.Normalize(packageId), out var version)
            ? version
            : throw new ArgumentException($"'{packageId}' is not in {python.LockFile}, so this project does not install it. Pass 'version' to look at a release anyway.");
    }
}
