using System.Text.Json;
using System.Text.RegularExpressions;

namespace Waymark.Core.Text;

/// <param name="LockFile">The lock file, relative to the workspace folder, with '/' separators.</param>
/// <param name="Manager">npm, pnpm or yarn.</param>
/// <param name="Versions">The exact version it pins per package name (scoped names keep their <c>@scope/</c>).</param>
public sealed record NodePins(string LockFile, string Manager, IReadOnlyDictionary<string, string> Versions);

/// <summary>
/// Reads the exact versions a Node lock file pins: package-lock.json (v1, v2, v3), pnpm-lock.yaml and yarn.lock (classic and berry). The nearest lock file
/// from the project folder up to the workspace folder is used. Nothing is run and no network is used; a lock file is only read.
/// </summary>
public static partial class NodeLockFiles
{
    private const long MaxLockBytes = 40_000_000;

    public static readonly IReadOnlyList<string> Names = ["package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml", "yarn.lock"];

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The pins of the nearest lock file, or null when there is none. A linked file is not followed.</summary>
    /// <exception cref="IOException">The lock file cannot be read.</exception>
    public static async Task<NodePins?> ReadAsync(string root, string folder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var top = Path.GetFullPath(root);
        var project = Path.GetFullPath(folder);
        var current = project;
        while (current.StartsWith(top, PathComparison))
        {
            foreach (var name in Names)
            {
                var candidate = new FileInfo(Path.Combine(current, name));
                if (!candidate.Exists || candidate.LinkTarget is not null || candidate.Length > MaxLockBytes)
                {
                    continue;
                }

                // The folder of the project below the folder of the lock file: 'apps/web' in a monorepo, empty at the root.
                var below = Path.GetRelativePath(current, project).Replace('\\', '/');
                if (below == ".")
                {
                    below = string.Empty;
                }

                var relative = Path.GetRelativePath(top, candidate.FullName).Replace('\\', '/');
                return name switch
                {
                    "pnpm-lock.yaml" => new NodePins(relative, "pnpm", ReadPnpm(await File.ReadAllTextAsync(candidate.FullName, ct).ConfigureAwait(false), below)),
                    "yarn.lock" => new NodePins(relative, "yarn", ReadYarn(await File.ReadAllTextAsync(candidate.FullName, ct).ConfigureAwait(false))),
                    _ => new NodePins(relative, "npm", await ReadNpmAsync(candidate.FullName, below, ct).ConfigureAwait(false)),
                };
            }

            if (current.Length <= top.Length)
            {
                break;
            }

            current = Path.GetDirectoryName(current) ?? top;
        }

        return null;
    }

    /// <summary>package-lock.json: <c>packages["node_modules/x"].version</c> (v2, v3) or <c>dependencies.x.version</c> (v1). The project's own node_modules entry wins over the hoisted one.</summary>
    private static async Task<Dictionary<string, string>> ReadNpmAsync(string path, string below, CancellationToken ct)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        var priority = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = document.RootElement;
        var own = below.Length == 0 ? null : below + "/node_modules/";
        if (root.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in packages.EnumerateObject())
            {
                var key = entry.Name;
                if (!entry.Value.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? name = null;
                var rank = 0;
                if (key.StartsWith("node_modules/", StringComparison.Ordinal) && key.IndexOf("/node_modules/", StringComparison.Ordinal) < 0)
                {
                    name = key["node_modules/".Length..];
                    rank = 1;
                }
                else if (own is not null && key.StartsWith(own, StringComparison.Ordinal) && key.IndexOf("/node_modules/", own.Length, StringComparison.Ordinal) < 0)
                {
                    name = key[own.Length..];
                    rank = 2;
                }

                var version = versionElement.GetString();
                if (name is null || version is null || (priority.TryGetValue(name, out var existing) && existing >= rank))
                {
                    continue;
                }

                versions[name] = version;
                priority[name] = rank;
            }
        }
        else if (root.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in dependencies.EnumerateObject())
            {
                if (entry.Value.TryGetProperty("version", out var versionElement) && versionElement.ValueKind == JsonValueKind.String && versionElement.GetString() is { } version)
                {
                    versions[entry.Name] = version;
                }
            }
        }

        return versions;
    }

    /// <summary>
    /// pnpm-lock.yaml: the direct dependencies of the project (<c>importers.&lt;folder&gt;</c> in v6+, the top level before that), each with a <c>version</c>
    /// such as <c>18.3.1(react@18.3.1)</c>; the peer suffix is dropped. A <c>link:</c> version is a workspace package and is left out.
    /// </summary>
    private static Dictionary<string, string> ReadPnpm(string text, string below)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var importerKey = below.Length == 0 ? "." : below;
        var start = Array.FindIndex(lines, l => l.StartsWith("importers:", StringComparison.Ordinal));
        int baseIndent;
        int first;
        int last;
        if (start >= 0)
        {
            var importer = Array.FindIndex(lines, start + 1, l => Unquote(l.Trim().TrimEnd(':')) == importerKey && l.StartsWith("  ", StringComparison.Ordinal) && !l.StartsWith("   ", StringComparison.Ordinal) && l.TrimEnd().EndsWith(':'));
            if (importer < 0)
            {
                return versions;
            }

            first = importer + 1;
            last = first;
            while (last < lines.Length && (lines[last].Length == 0 || lines[last].StartsWith("    ", StringComparison.Ordinal)))
            {
                last++;
            }

            baseIndent = 4;
        }
        else
        {
            first = 0;
            last = Array.FindIndex(lines, l => l.StartsWith("packages:", StringComparison.Ordinal) || l.StartsWith("snapshots:", StringComparison.Ordinal));
            last = last < 0 ? lines.Length : last;
            baseIndent = 0;
        }

        // Inside the block: '<group>:' at baseIndent (dependencies, devDependencies, optionalDependencies), then '<name>:' two deeper with 'version:' four deeper.
        var inGroup = false;
        string? currentName = null;
        for (var i = first; i < last; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var indent = line.Length - line.TrimStart().Length;
            var content = line.Trim();
            if (indent == baseIndent)
            {
                inGroup = content is "dependencies:" or "devDependencies:" or "optionalDependencies:";
                currentName = null;
            }
            else if (inGroup && indent == baseIndent + 2 && content.EndsWith(':'))
            {
                currentName = Unquote(content[..^1]);
            }
            else if (inGroup && currentName is not null && indent == baseIndent + 4 && content.StartsWith("version:", StringComparison.Ordinal))
            {
                var value = Unquote(content["version:".Length..].Trim());
                var paren = value.IndexOf('(', StringComparison.Ordinal);
                if (paren >= 0)
                {
                    value = value[..paren];
                }

                if (value.Length > 0 && !value.StartsWith("link:", StringComparison.Ordinal) && !value.StartsWith("file:", StringComparison.Ordinal))
                {
                    versions[currentName] = value;
                }
            }
        }

        return versions;
    }

    /// <summary>yarn.lock: an entry heading lists the requested ranges (<c>"react@^18.2.0", react@^18.0.0:</c>) and a <c>version</c> line follows; the first version seen for a name stays.</summary>
    private static Dictionary<string, string> ReadYarn(string text)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (rawLine.Length == 0 || rawLine[0] == '#')
            {
                continue;
            }

            if (rawLine[0] != ' ')
            {
                names.Clear();
                foreach (var part in rawLine.TrimEnd(':').Split(','))
                {
                    var spec = Unquote(part.Trim());
                    var at = spec.LastIndexOf('@');
                    if (at > 0)
                    {
                        names.Add(spec[..at]);
                    }
                }

                continue;
            }

            var line = rawLine.Trim();
            var match = YarnVersion().Match(line);
            if (match.Success && names.Count > 0)
            {
                foreach (var name in names)
                {
                    versions.TryAdd(name, match.Groups["version"].Value);
                }

                names.Clear();
            }
        }

        return versions;
    }

    private static string Unquote(string text) =>
        text.Length >= 2 && (text[0] == '"' || text[0] == '\'') && text[^1] == text[0] ? text[1..^1] : text;

    [GeneratedRegex(@"^version:?\s+""?(?<version>[^""\s]+)""?$", RegexOptions.None, 200)]
    private static partial Regex YarnVersion();
}
