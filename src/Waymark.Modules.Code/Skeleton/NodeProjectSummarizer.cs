using System.Globalization;
using System.Text.Json;
using Waymark.Core.Security;
using Waymark.Core.Text;
using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Skeleton;

/// <summary>
/// Renders the Node projects under the viewed folder, the way <see cref="ProjectFileSummarizer"/> renders the .NET projects and
/// <see cref="PythonProjectSummarizer"/> the Python ones: the declared dependencies with the version the lock file pins (package-lock.json, pnpm-lock.yaml,
/// yarn.lock), so an agent knows which version to look up before it calls a package. One project per package.json; nothing is run.
/// </summary>
internal sealed class NodeProjectSummarizer(FileAccessPolicy policy, SkeletonOptions options)
{
    private const long MaxManifestBytes = 2_000_000;
    private const int MaxProjectsParsed = 40;
    private const int MaxScriptsListed = 14;
    private const string NotLocked = "not locked";

    private static readonly string[] DependencyGroups = ["dependencies", "devDependencies", "peerDependencies", "optionalDependencies"];

    private static readonly JsonDocumentOptions ManifestOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private sealed record Dependency(string Name, string Spec, string Group);

    private sealed record NodeFacts(
        string RelativePath,
        string? Name,
        string? Version,
        string Manager,
        string? LockFile,
        IReadOnlyDictionary<string, string> Locked,
        IReadOnlyList<Dependency> Dependencies,
        IReadOnlyList<string> Scripts,
        string? Workspaces,
        string? Engines,
        string? Error);

    /// <summary>Markdown for the Node projects below the folder; empty when there are none.</summary>
    public async Task<string> RenderAsync(
        string root,
        string scopeAbsolute,
        string scopeRelative,
        string workspaceName,
        WorkspaceAccessRules rules,
        bool overview,
        int maxChars,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var (manifests, truncated) = VisibleEntries.Scan(root, scopeAbsolute, policy, rules, file => file.Name.Equals("package.json", StringComparison.OrdinalIgnoreCase), options.MaxScanEntries, options.MaxListedFiles, ct);
        if (manifests.Count == 0)
        {
            return string.Empty;
        }

        var ordered = manifests.OrderBy(m => m.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        var facts = new List<NodeFacts>(Math.Min(ordered.Count, MaxProjectsParsed));
        foreach (var manifest in ordered.Take(MaxProjectsParsed))
        {
            ct.ThrowIfCancellationRequested();
            facts.Add(await ParseAsync(root, manifest, ct).ConfigureAwait(false));
        }

        var output = new OutputBudget(maxChars);
        if (!overview || ordered.Count <= options.MaxProjectsInline)
        {
            foreach (var project in facts)
            {
                AppendProject(project, output);
            }
        }
        else
        {
            AppendSummary(facts, ordered.Count, scopeRelative, workspaceName, output);
        }

        if (truncated || ordered.Count > MaxProjectsParsed)
        {
            output.TryAppendLine($"_More package.json files exist than are listed (limit {MaxProjectsParsed.ToString(CultureInfo.InvariantCulture)} parsed, or the workspace is very large)._");
        }

        return output.Build().TrimEnd();
    }

    private static async Task<NodeFacts> ParseAsync(string root, VisibleFile manifest, CancellationToken ct)
    {
        var folder = manifest.Info.DirectoryName ?? root;
        try
        {
            if (manifest.Info.Length > MaxManifestBytes)
            {
                return Failed(manifest, "The package.json is too large to read");
            }

            await using var stream = new FileStream(manifest.Info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            using var document = await JsonDocument.ParseAsync(stream, ManifestOptions, ct).ConfigureAwait(false);
            var json = document.RootElement;
            if (json.ValueKind != JsonValueKind.Object)
            {
                return Failed(manifest, "The package.json is not an object");
            }

            var pins = await NodeLockFiles.ReadAsync(root, folder, ct).ConfigureAwait(false);
            var dependencies = new List<Dependency>();
            foreach (var group in DependencyGroups)
            {
                if (json.TryGetProperty(group, out var table) && table.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in table.EnumerateObject())
                    {
                        dependencies.Add(new Dependency(entry.Name, entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() ?? string.Empty : string.Empty, group));
                    }
                }
            }

            var scripts = json.TryGetProperty("scripts", out var scriptTable) && scriptTable.ValueKind == JsonValueKind.Object
                ? scriptTable.EnumerateObject().Select(s => s.Name).ToList()
                : [];
            var manager = pins?.Manager ?? ManagerField(json) ?? "npm";
            return new NodeFacts(
                manifest.RelativePath,
                Text(json, "name"),
                Text(json, "version"),
                manager,
                pins?.LockFile,
                pins?.Versions ?? new Dictionary<string, string>(),
                dependencies,
                scripts,
                WorkspacesText(json),
                json.TryGetProperty("engines", out var engines) && engines.ValueKind == JsonValueKind.Object ? Text(engines, "node") : null,
                null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Failed(manifest, ex is JsonException ? "The package.json is not valid JSON" : "The package.json cannot be read");
        }
    }

    private static NodeFacts Failed(VisibleFile manifest, string error) =>
        new(manifest.RelativePath, null, null, "npm", null, new Dictionary<string, string>(), [], [], null, null, error);

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The tool named by the packageManager field (<c>pnpm@9.1.0</c> is pnpm).</summary>
    private static string? ManagerField(JsonElement json)
    {
        var field = Text(json, "packageManager");
        var at = field?.IndexOf('@', StringComparison.Ordinal) ?? -1;
        return field is null ? null : at > 0 ? field[..at] : field;
    }

    /// <summary>The workspaces globs, from the array form or the object form of the field.</summary>
    private static string? WorkspacesText(JsonElement json)
    {
        if (!json.TryGetProperty("workspaces", out var workspaces))
        {
            return null;
        }

        var array = workspaces.ValueKind == JsonValueKind.Object && workspaces.TryGetProperty("packages", out var packages) ? packages : workspaces;
        if (array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = array.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()).OfType<string>().ToList();
        return items.Count == 0 ? null : string.Join(", ", items);
    }

    private static void AppendProject(NodeFacts project, OutputBudget output)
    {
        output.TryAppendLine($"## Node Dependencies: {project.RelativePath}");
        output.TryAppendLine();
        if (project.Error is { } error)
        {
            output.TryAppendLine($"*[{error}]*");
            output.TryAppendLine();
            return;
        }

        var direct = project.Dependencies.Where(d => d.Group == "dependencies").ToList();
        var others = project.Dependencies.Where(d => d.Group != "dependencies").ToList();
        if (project.Dependencies.Count == 0)
        {
            output.TryAppendLine("*No dependencies declared*");
        }

        if (direct.Count > 0)
        {
            output.TryAppendLine($"**Packages ({direct.Count.ToString(CultureInfo.InvariantCulture)}):**");
            foreach (var dependency in direct)
            {
                output.TryAppendLine($"• {Describe(dependency, project.Locked)}");
            }
        }

        if (others.Count > 0)
        {
            output.TryAppendLine($"**Development, peer and optional packages ({others.Count.ToString(CultureInfo.InvariantCulture)}):**");
            foreach (var dependency in others)
            {
                output.TryAppendLine($"• [{ShortGroup(dependency.Group)}] {Describe(dependency, project.Locked)}");
            }
        }

        output.TryAppendLine();
        if (project.Name is not null)
        {
            output.TryAppendLine($"**Name:** {project.Name}{(project.Version is null ? string.Empty : " " + project.Version)}");
        }

        var typescript = project.Locked.TryGetValue("typescript", out var tsVersion)
            ? tsVersion
            : project.Dependencies.FirstOrDefault(d => d.Name == "typescript")?.Spec;
        output.TryAppendLine($"**Package manager:** {project.Manager}{(project.LockFile is null ? " (no lock file found)" : $" (lock: {project.LockFile}, {project.Locked.Count.ToString(CultureInfo.InvariantCulture)} packages locked)")}");
        if (typescript is not null)
        {
            output.TryAppendLine($"**TypeScript:** {typescript}");
        }

        if (project.Engines is not null)
        {
            output.TryAppendLine($"**Node:** {project.Engines}");
        }

        if (project.Workspaces is not null)
        {
            output.TryAppendLine($"**Workspaces:** {project.Workspaces}");
        }

        if (project.Scripts.Count > 0)
        {
            var shown = project.Scripts.Take(MaxScriptsListed);
            var more = project.Scripts.Count > MaxScriptsListed ? ", ..." : string.Empty;
            output.TryAppendLine($"**Scripts:** {string.Join(", ", shown)}{more}");
        }

        output.TryAppendLine();
    }

    private static void AppendSummary(List<NodeFacts> projects, int total, string scopeRelative, string workspaceName, OutputBudget output)
    {
        var scope = scopeRelative.Length == 0 ? "the workspace" : $"`{scopeRelative}`";
        output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"## Node Dependencies: {total} projects in {scope}"));
        output.TryAppendLine();
        foreach (var project in projects)
        {
            var locked = project.LockFile is null ? "no lock" : "locked";
            output.TryAppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"• `{project.RelativePath}` - {project.Name ?? "unnamed"}, {project.Manager}, {locked}, {project.Dependencies.Count} dependencies"));
        }

        output.TryAppendLine();
        output.TryAppendLine($"_One project's own pins: code_skeleton(\"{workspaceName}\", path=\"<project folder>\")._");
        output.TryAppendLine();
    }

    /// <summary>The exact locked version, with the declared range when it says more; else the declared range and a note that nothing pins it.</summary>
    private static string Describe(Dependency dependency, IReadOnlyDictionary<string, string> locked)
    {
        if (locked.TryGetValue(dependency.Name, out var version))
        {
            var same = dependency.Spec.Length == 0 || dependency.Spec == version;
            return same ? $"{dependency.Name} `{version}`" : $"{dependency.Name} `{version}` (declared {dependency.Spec})";
        }

        return dependency.Spec.Length == 0
            ? $"{dependency.Name} `unpinned` ({NotLocked})"
            : $"{dependency.Name} `{dependency.Spec}` ({NotLocked})";
    }

    private static string ShortGroup(string group) => group switch
    {
        "devDependencies" => "dev",
        "peerDependencies" => "peer",
        "optionalDependencies" => "optional",
        _ => group,
    };
}
