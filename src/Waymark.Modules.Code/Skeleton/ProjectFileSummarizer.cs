using System.Xml;
using System.Xml.Linq;
using Waymark.Core.Security;
using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Skeleton;

/// <summary>
/// Appends the solution files and a compact NuGet summary of every project file. Versions that live in
/// Directory.Packages.props and a target framework that lives in Directory.Build.props are resolved.
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

    public async Task AppendAsync(string root, OutputBudget output, CancellationToken ct)
    {
        List<FileInfo> topLevel;
        try
        {
            topLevel = VisibleEntries.Files(root, root, policy);
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
                await AppendSolutionAsync(file, title, language, output, ct).ConfigureAwait(false);
            }
        }

        var (projects, truncated) = VisibleEntries.Scan(
            root,
            policy,
            file => file.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase),
            options.MaxScanEntries,
            options.MaxListedFiles,
            ct);

        var centralVersions = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            AppendProject(root, project, centralVersions, output);
        }

        if (truncated)
        {
            output.TryAppendLine($"_More project files exist than are listed (limit {options.MaxListedFiles}, or the workspace is very large)._");
        }
    }

    private async Task AppendSolutionAsync(FileInfo file, string title, string language, OutputBudget output, CancellationToken ct)
    {
        output.TryAppendLine($"## {title}");
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

    private void AppendProject(string root, VisibleFile project, Dictionary<string, Dictionary<string, string>> centralVersions, OutputBudget output)
    {
        output.TryAppendLine($"## NuGet Dependencies: {project.RelativePath}");
        output.TryAppendLine();
        try
        {
            var document = Load(project.Info.FullName);
            var projectFolder = project.Info.DirectoryName ?? root;
            var central = LoadCentralVersions(root, projectFolder, centralVersions);
            var packages = Elements(document, "PackageReference")
                .Select(reference => (Name: Attribute(reference, "Include") ?? Unknown, Reference: reference))
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (packages.Count == 0)
            {
                output.TryAppendLine("*No NuGet packages*");
            }
            else
            {
                output.TryAppendLine($"**Packages ({packages.Count}):**");
                foreach (var (name, reference) in packages)
                {
                    output.TryAppendLine($"• {name} `{ResolveVersion(reference, name, central)}`");
                }
            }

            output.TryAppendLine();
            output.TryAppendLine($"**SDK:** {Attribute(document.Root, "Sdk") ?? Unknown}");
            output.TryAppendLine($"**Target Framework:** {ResolveFramework(root, projectFolder, document)}");
        }
        catch (XmlException ex)
        {
            output.TryAppendLine($"*[Error parsing the project file: {ex.Message}]*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output.TryAppendLine("*[The project file cannot be read]*");
        }

        output.TryAppendLine();
    }

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
}
