using System.Text.RegularExpressions;

namespace Waymark.Core.Text;

/// <param name="LockFile">The lock file, relative to the workspace folder, with '/' separators.</param>
/// <param name="Versions">The exact version it pins per package; the keys are normalised names (see <see cref="PythonLockFiles.Normalize"/>).</param>
public sealed record PythonPins(string LockFile, IReadOnlyDictionary<string, string> Versions);

/// <summary>
/// Reads the exact versions a Python lock file pins. uv.lock, poetry.lock and pdm.lock list <c>[[package]]</c> tables and pylock.toml (PEP 751) lists
/// <c>[[packages]]</c>; every one has a name and a version. The nearest lock file from the project folder up to the workspace folder is used.
/// </summary>
public static partial class PythonLockFiles
{
    private const int MaxLockChars = 4_000_000;

    public static readonly IReadOnlyList<string> Names = ["uv.lock", "poetry.lock", "pdm.lock", "pylock.toml"];

    private static readonly FileStreamOptions ReadOptions = new()
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
    };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The pins of the nearest lock file, or null when there is none. A linked file is not followed.</summary>
    /// <exception cref="IOException">The lock file cannot be read.</exception>
    public static async Task<PythonPins?> ReadAsync(string root, string folder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var current = Path.GetFullPath(folder);
        var top = Path.GetFullPath(root);
        while (current.StartsWith(top, PathComparison))
        {
            foreach (var name in Names)
            {
                var candidate = new FileInfo(Path.Combine(current, name));
                if (!candidate.Exists || candidate.LinkTarget is not null)
                {
                    continue;
                }

                var text = await ReadTextAsync(candidate.FullName, ct).ConfigureAwait(false);
                var versions = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var section in TomlLite.Parse(text))
                {
                    if (!section.IsArrayItem || section.Name is not ("package" or "packages"))
                    {
                        continue;
                    }

                    var packageName = TomlLite.AsString(section.Values.GetValueOrDefault("name"));
                    var version = TomlLite.AsString(section.Values.GetValueOrDefault("version"));
                    if (packageName is not null && version is not null)
                    {
                        versions[Normalize(packageName)] = version;
                    }
                }

                return new PythonPins(Path.GetRelativePath(top, candidate.FullName).Replace('\\', '/'), versions);
            }

            if (current.Length <= top.Length)
            {
                break;
            }

            current = Path.GetDirectoryName(current) ?? top;
        }

        return null;
    }

    /// <summary>The tool a lock file belongs to: uv, poetry, pdm or pylock.</summary>
    public static string ToolOf(string lockFile)
    {
        var name = Path.GetFileName(lockFile);
        return name.StartsWith("uv", StringComparison.OrdinalIgnoreCase) ? "uv"
            : name.StartsWith("poetry", StringComparison.OrdinalIgnoreCase) ? "poetry"
            : name.StartsWith("pdm", StringComparison.OrdinalIgnoreCase) ? "pdm"
            : "pylock";
    }

    /// <summary>PEP 503 normalisation: lower case, every run of '-', '_' and '.' becomes one '-'.</summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return SeparatorRun().Replace(name.Trim(), "-").ToLowerInvariant();
    }

    private static async Task<string> ReadTextAsync(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, ReadOptions);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxLockChars + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
        return new string(buffer, 0, Math.Min(read, MaxLockChars));
    }

    [GeneratedRegex(@"[-_.]+", RegexOptions.None, 200)]
    private static partial Regex SeparatorRun();
}
