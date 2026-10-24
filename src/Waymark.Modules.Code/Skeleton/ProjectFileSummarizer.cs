using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Waymark.Core.Security;
using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Skeleton;

/// <summary>
/// Renders the solution files and the NuGet summary of the project files under the viewed folder. Versions that live in
/// Directory.Packages.props and a target framework that lives in Directory.Build.props are resolved. Up to
/// SkeletonOptions.MaxProjectsInline projects (or always, when a depth was asked for) each project gets its own block; above it
/// the overview shows one table of packages with every pinned version, so version drift stays visible.
/// </summary>
internal sealed class ProjectFileSummarizer(FileAccessPolicy policy, SkeletonOptions options)
{
    private const string CentralPackagesFile = "Directory.Packages.props";
    private const string SharedBuildPropsFile = "Directory.Build.props";
    private const string Unknown = "Unknown";
    private const int MaxXmlChars = 2_000_000;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxXmlChars,
    };

    private static readonly (string Extension, string Title, string Language)[] SolutionKinds =
    [
        (".sln", "Solution File (.sln)", "text"),
        (".slnx", "Solution File (.slnx)", "xml"),
    ];

    private static readonly FileStreamOptions ReadOptions = new()
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
    };

    /// <param name="root">Absolute workspace folder.</param>
    /// <param name="scopeAbsolute">Absolute folder whose projects are summarised.</param>
    /// <param name="scopeRelative">That folder relative to the root; empty for the root, which also shows the solution files.</param>
    /// <param name="overview">No depth was asked for: big solution files are named only and many projects are summarised.</param>
    /// <param name="maxChars">Size limit of the returned markdown.</param>
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
        var output = new OutputBudget(maxChars);
        if (scopeRelative.Length == 0)
        {
            await AppendSolutionsAsync(root, rules, overview, output, ct).ConfigureAwait(false);
        }

        var (projects, truncated) = VisibleEntries.Scan(
            root,
            scopeAbsolute,
            policy,
            rules,
            file => file.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase),
            options.MaxScanEntries,
            options.MaxListedFiles,
            ct);

        var centralVersions = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var facts = new List<ProjectFacts>(projects.Count);
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            facts.Add(Parse(root, project, centralVersions));
        }

        if (!overview || facts.Count <= options.MaxProjectsInline)
        {
            foreach (var project in facts)
            {
                AppendProject(project, output);
            }
        }
        else
        {
            AppendSummary(facts, scopeRelative, workspaceName, output);
        }

        if (truncated)
        {
            output.TryAppendLine($"_More project files exist than are listed (limit {options.MaxListedFiles}, or the workspace is very large)._");
        }

        return output.Build().TrimEnd();
    }

    private async Task AppendSolutionsAsync(string root, WorkspaceAccessRules rules, bool overview, OutputBudget output, CancellationToken ct)
    {
        List<FileInfo> topLevel;
        try
        {
            topLevel = VisibleEntries.Files(root, root, policy, rules);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output.TryAppendLine("*[The workspace folder cannot be listed]*");
            return;
        }

        foreach (var (extension, title, language) in SolutionKinds)
        {
            foreach (var file in topLevel.Where(f => f.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase)))
            {
                await AppendSolutionAsync(file, title, language, overview, output, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task AppendSolutionAsync(FileInfo file, string title, string language, bool overview, OutputBudget output, CancellationToken ct)
    {
        output.TryAppendLine($"## {title}");
        if (overview && file.Length > options.SolutionInlineChars)
        {
            output.TryAppendLine($"**File:** `{file.Name}` ({FileFacts.FormatSize(file.Length)}) - read it with code_read; the projects are listed below.");
            output.TryAppendLine();
            return;
        }

        output.TryAppendLine($"**File:** `{file.Name}`");
        output.TryAppendLine();
        try
        {
            var (text, cut) = await ReadCappedAsync(file.FullName, options.MaxSolutionChars, ct).ConfigureAwait(false);
            output.TryAppendLine($"```{language}");
            output.TryAppendLine(text.TrimEnd());
            output.TryAppendLine("```");
            if (cut)
            {
                output.TryAppendLine($"_File cut at {options.MaxSolutionChars} characters._");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output.TryAppendLine("*[This file cannot be read]*");
        }

        output.TryAppendLine();
    }

    private ProjectFacts Parse(string root, VisibleFile project, Dictionary<string, Dictionary<string, string>> centralVersions)
    {
        try
        {
            var document = Load(project.Info.FullName);
            var projectFolder = project.Info.DirectoryName ?? root;
            var central = LoadCentralVersions(root, projectFolder, centralVersions);
            var packages = Elements(document, "PackageReference")
                .Select(reference =>
                {
                    var name = Attribute(reference, "Include") ?? Unknown;
                    return new PackagePin(name, ResolveVersion(reference, name, central));
                })
                .OrderBy(pin => pin.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new ProjectFacts(
                project.RelativePath,
                packages,
                Attribute(document.Root, "Sdk") ?? Unknown,
                ResolveFramework(root, projectFolder, document),
                null);
        }
        catch (XmlException ex)
        {
            return ProjectFacts.Failed(project.RelativePath, $"Error parsing the project file: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ProjectFacts.Failed(project.RelativePath, "The project file cannot be read");
        }
    }

    private static void AppendProject(ProjectFacts project, OutputBudget output)
    {
        output.TryAppendLine($"## NuGet Dependencies: {project.RelativePath}");
        output.TryAppendLine();
        if (project.Error is { } error)
        {
            output.TryAppendLine($"*[{error}]*");
            output.TryAppendLine();
            return;
        }

        if (project.Packages.Count == 0)
        {
            output.TryAppendLine("*No NuGet packages*");
        }
        else
        {
            output.TryAppendLine($"**Packages ({project.Packages.Count}):**");
            foreach (var pin in project.Packages)
            {
                output.TryAppendLine($"• {pin.Name} `{pin.Version}`");
            }
        }

        output.TryAppendLine();
        output.TryAppendLine($"**SDK:** {project.Sdk}");
        output.TryAppendLine($"**Target Framework:** {project.Framework}");
        output.TryAppendLine();
    }

    private static void AppendSummary(List<ProjectFacts> projects, string scopeRelative, string workspaceName, OutputBudget output)
    {
        var parsed = projects.Where(project => project.Error is null).ToList();
        var scope = scopeRelative.Length == 0 ? "the workspace" : $"`{scopeRelative}`";
        output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"## NuGet Dependencies: {projects.Count} projects in {scope}"));
        output.TryAppendLine();
        output.TryAppendLine($"**Target frameworks:** {Counted(parsed.Select(project => project.Framework))}");
        output.TryAppendLine($"**SDKs:** {Counted(parsed.Select(project => project.Sdk))}");
        output.TryAppendLine();

        var packages = parsed
            .SelectMany(project => project.Packages)
            .GroupBy(pin => pin.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (packages.Count == 0)
        {
            output.TryAppendLine("*No NuGet packages*");
        }
        else
        {
            output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"**Packages ({packages.Count}), with every pinned version and how many projects pin it:**"));
            foreach (var package in packages)
            {
                var versions = package
                    .GroupBy(pin => pin.Version, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(version => version.Count())
                    .ThenBy(version => version.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(version => string.Create(CultureInfo.InvariantCulture, $"`{version.Key}` ({version.Count()})"));
                output.TryAppendLine($"• {package.Key} {string.Join(", ", versions)}");
            }
        }

        var failed = projects.Count - parsed.Count;
        if (failed > 0)
        {
            output.TryAppendLine();
            output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"_{failed} project file(s) could not be read or parsed; open their folder to see why._"));
        }

        output.TryAppendLine();
        output.TryAppendLine($"_One project's own pins, SDK and target framework: code_skeleton(\"{workspaceName}\", path=\"<project folder>\")._");
        output.TryAppendLine();
    }

    private static string Counted(IEnumerable<string> values) =>
        string.Join(", ", values
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => string.Create(CultureInfo.InvariantCulture, $"{group.Key} ({group.Count()})")));

    private string ResolveFramework(string root, string projectFolder, XDocument project)
    {
        var own = FrameworkOf(project);
        if (own is not null)
        {
            return own;
        }

        var shared = FindNearest(root, projectFolder, SharedBuildPropsFile);
        return shared is null ? Unknown : FrameworkOf(Load(shared)) ?? Unknown;
    }

    private static string? FrameworkOf(XDocument document) =>
        Elements(document, "TargetFramework").Concat(Elements(document, "TargetFrameworks"))
            .Select(element => element.Value.Trim())
            .FirstOrDefault(value => value.Length > 0);

    private static string ResolveVersion(XElement reference, string name, Dictionary<string, string> central) =>
        Attribute(reference, "Version")
        ?? Attribute(reference, "VersionOverride")
        ?? reference.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value.Trim()
        ?? (central.TryGetValue(name, out var version) ? version : null)
        ?? Unknown;

    private Dictionary<string, string> LoadCentralVersions(string root, string projectFolder, Dictionary<string, Dictionary<string, string>> cache)
    {
        var path = FindNearest(root, projectFolder, CentralPackagesFile);
        if (path is null)
        {
            return [];
        }

        if (cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in Elements(Load(path), "PackageVersion"))
        {
            var name = Attribute(element, "Include");
            var version = Attribute(element, "Version");
            if (name is not null && version is not null)
            {
                versions[name] = version;
            }
        }

        cache[path] = versions;
        return versions;
    }

    // The nearest file of that name from the project folder up to the workspace root. A linked file is not followed.
    private static string? FindNearest(string root, string startFolder, string fileName)
    {
        var current = startFolder;
        while (current.StartsWith(root, PathComparison))
        {
            var candidate = new FileInfo(Path.Combine(current, fileName));
            if (candidate.Exists && candidate.LinkTarget is null)
            {
                return candidate.FullName;
            }

            if (current.Length <= root.Length)
            {
                break;
            }

            current = Path.GetDirectoryName(current) ?? root;
        }

        return null;
    }

    private static XDocument Load(string path)
    {
        using var stream = new FileStream(path, ReadOptions);
        using var reader = XmlReader.Create(stream, ReaderSettings);
        return XDocument.Load(reader);
    }

    private static IEnumerable<XElement> Elements(XDocument document, string localName) =>
        document.Descendants().Where(element => element.Name.LocalName == localName);

    private static string? Attribute(XElement? element, string name)
    {
        var value = element?.Attribute(name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static async Task<(string Text, bool Cut)> ReadCappedAsync(string path, int maxChars, CancellationToken ct)
    {
        using var stream = new FileStream(path, ReadOptions);
        using var reader = new StreamReader(stream);
        var buffer = new char[maxChars + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
        return read > maxChars ? (new string(buffer, 0, maxChars), true) : (new string(buffer, 0, read), false);
    }

    private sealed record PackagePin(string Name, string Version);

    private sealed record ProjectFacts(string RelativePath, IReadOnlyList<PackagePin> Packages, string Sdk, string Framework, string? Error)
    {
        public static ProjectFacts Failed(string relativePath, string error) => new(relativePath, [], Unknown, Unknown, error);
    }
}
