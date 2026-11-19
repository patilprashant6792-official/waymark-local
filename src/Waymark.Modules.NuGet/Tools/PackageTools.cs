using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Waymark.Core.NuGetIndex;
using Waymark.Modules.NuGet.Indexing;
using Waymark.Modules.NuGet.Loading;
using Waymark.Modules.NuGet.Pypi;

namespace Waymark.Modules.NuGet.Tools;

/// <summary>
/// The four package lookups. One set of tools serves every ecosystem (NuGet, PyPI and npm): the ecosystem is an argument, so the tool count stays the same
/// when a language is added.
/// </summary>
[McpServerToolType]
public sealed class PackageTools(INuGetSymbolService symbols, IPackagePins pins, ILogger<PackageTools> logger)
{
    private const string EcosystemHelp = "Optional: 'nuget' (default), 'pypi' (a Python package) or 'npm' (a JavaScript or TypeScript package).";
    private const string WorkspaceHelp = "Optional, pypi and npm only: workspace name. With it, and no version, the version comes from the project's lock file (uv.lock, poetry.lock, pdm.lock, pylock.toml; package-lock.json, pnpm-lock.yaml, yarn.lock).";
    private const string ProjectHelp = "Optional, pypi and npm only: the project folder (or its pyproject.toml or package.json) inside the workspace whose lock file decides the version; default the workspace folder.";

    [McpServerTool(Name = "pkg_search", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Finds a package when you do not know its exact id. Each hit: id, latest version, download count when the registry gives one, and a one-line description; " +
        "the reply ends with the pkg_find call to make next. Reads the public registry and changes nothing.\n" +
        "How each ecosystem searches: NuGet (default) searches nuget.org by keyword. npm (ecosystem='npm') searches the npm registry by keyword. " +
        "PyPI (ecosystem='pypi') has no search, so the query is tried as project names: the words joined with '-', then each word alone; only projects that exist come back, and none means no project has that name.\n" +
        "WHEN TO USE: only to discover or confirm a package id. Skip it when the id is already in the project's package list or lock file and go straight to pkg_find; " +
        "for the namespaces or modules of a known package use pkg_outline.")]
    public Task<string> Search(
        [Description("NuGet and npm: keywords or a name ('polly retry', 'react query'). PyPI: the project name as in 'pip install name' ('requests'); words are joined with '-' ('scikit learn' tries scikit-learn).")]
        string query,
        [Description(EcosystemHelp)]
        string? ecosystem = null,
        [Description("Optional: number of packages to return (default 5, max 8)")]
        int take = 5,
        [Description("Optional: include prerelease versions (default false; NuGet only)")]
        bool includePrerelease = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(null, null, () => symbols.PackagesAsync(PackageEcosystems.Parse(ecosystem), query, take, includePrerelease, cancellationToken));

    [McpServerTool(Name = "pkg_outline", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Shows the map of a package (namespaces for NuGet, modules for PyPI and npm, each with a count), never a listing of types. " +
        "Use it for a completely unfamiliar package, or with refresh=true after a version bump. Otherwise go straight to pkg_find. " +
        "The first call for a package version downloads and indexes it, which can take a few seconds; later calls are instant. " +
        "Pin the version (and for NuGet the targetFramework) of the project so the API matches what the project runs against. " +
        "PyPI packages are read from their wheel and npm packages from their declaration files, without installing, importing or running anything.")]
    public Task<string> Outline(
        [Description("Package id (NuGet, e.g. 'Polly'), project name (PyPI, e.g. 'requests') or package name (npm, e.g. 'zod' or '@tanstack/react-query')")]
        string packageId,
        [Description(EcosystemHelp)]
        string? ecosystem = null,
        [Description("Optional: only namespaces (modules) that start with this text")]
        string? @namespace = null,
        [Description("Optional: rebuild the index from the package instead of reading the stored one (default false)")]
        bool refresh = false,
        [Description("Optional: exact package version, from the project's package list or lock file. Omit for the latest stable.")]
        string? version = null,
        [Description("Optional, NuGet only: target framework such as 'net10.0' (default: the configured default)")]
        string? targetFramework = null,
        [Description("Optional: allow prerelease when no version is given (default false; NuGet only)")]
        bool includePrerelease = false,
        [Description("Optional, NuGet only: extra package ids whose assemblies the reflection needs, only after an error that names a missing assembly")]
        string[]? additionalPackageIds = null,
        [Description(WorkspaceHelp)]
        string? workspace = null,
        [Description(ProjectHelp)]
        string? project = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(packageId, version, async () => await symbols.OutlineAsync(
            await RequestAsync(packageId, ecosystem, version, targetFramework, includePrerelease, additionalPackageIds, workspace, project, cancellationToken).ConfigureAwait(false),
            @namespace,
            refresh,
            cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "pkg_find", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Finds types, classes, functions and methods inside one package, by name or by describing what you need ('chat completion', 'retry policy'). " +
        "Each hit is a symbol id with a one-line card (a type: kind, full name, member counts and summary; a method: parameter types and return type), ranked by relevance and how prominent the symbol is in the package; the reply gives the total and the next call. " +
        "An empty query lists the package's most prominent symbols. Nothing matched gives hints instead of an error. " +
        "The first call for a package version downloads and indexes it (a few seconds); later calls are instant. Reads only.\n" +
        "Narrow with kind, namespace (exact; the module for PyPI and npm) or declaringType (all methods of one type; for PyPI and npm a module name lists its functions).\n" +
        "Pin the version the project uses: pass version, or for PyPI and npm pass workspace so the lock file decides.\n" +
        "WHEN TO USE: the usual first call for a known package. Before it, pkg_search only when the package id is unknown, and pkg_outline only when the package is completely unfamiliar. " +
        "Next: pkg_expand with up to 5 of the ids for exact signatures. For code in the workspace itself use code_search.")]
    public Task<string> Find(
        [Description("Package id (NuGet, e.g. 'Polly'), project name (PyPI, e.g. 'requests') or package name (npm, e.g. 'zod' or '@tanstack/react-query')")]
        string packageId,
        [Description("What you are looking for: a name, camel-case or snake_case words, or a description. Empty browses.")]
        string query = "",
        [Description(EcosystemHelp)]
        string? ecosystem = null,
        [Description("Optional: class, interface, enum, struct, delegate or method (a PyPI or npm function is a method; an npm type alias is a struct)")]
        string? kind = null,
        [Description("Optional: exact namespace to search in (the module for PyPI and npm: a dotted module, or a path inside the package)")]
        string? @namespace = null,
        [Description("Optional: exact type name; lists that type's methods (for PyPI and npm a module name lists its functions)")]
        string? declaringType = null,
        [Description("Optional: results to return (default 10, max 25)")]
        int limit = 10,
        [Description("Optional: exact package version, from the project's package list or lock file. Omit for the latest stable.")]
        string? version = null,
        [Description("Optional, NuGet only: target framework such as 'net10.0' (default: the configured default)")]
        string? targetFramework = null,
        [Description("Optional: allow prerelease when no version is given (default false; NuGet only)")]
        bool includePrerelease = false,
        [Description("Optional, NuGet only: extra package ids whose assemblies the reflection needs, only after an error that names a missing assembly")]
        string[]? additionalPackageIds = null,
        [Description(WorkspaceHelp)]
        string? workspace = null,
        [Description(ProjectHelp)]
        string? project = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(packageId, version, async () => await symbols.FindAsync(
            await RequestAsync(packageId, ecosystem, version, targetFramework, includePrerelease, additionalPackageIds, workspace, project, cancellationToken).ConfigureAwait(false),
            query,
            kind,
            @namespace,
            declaringType,
            limit,
            cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "pkg_expand", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Shows the exact API of up to 5 symbols of a package: constructor and overload signatures, methods, properties, enum values, documentation (XML comments, docstrings or JSDoc) and the ids of related types. " +
        "The signatures are read from the package itself (NuGet: the compiled assembly; PyPI: the wheel, plus its stub package when it has no types; npm: the declaration files or the @types package), " +
        "so they are the source of truth for what exists: write calls from them, not from memory. Reads only.\n" +
        "An id that is not in the package is reported as not found while the others are still shown; more than 5 is an error. The first call for a package version downloads and indexes it.\n" +
        "WHEN TO USE: after pkg_find gave you ids, before you write code that calls them; pass a related id back to follow a parameter or return type. " +
        "To locate symbols use pkg_find, for the namespace map pkg_outline, to find a package id pkg_search.")]
    public Task<string> Expand(
        [Description("Package id (NuGet), project name (PyPI) or package name (npm)")]
        string packageId,
        [Description("Up to 5 symbol ids from pkg_find or from a 'rel' section; a full or trailing type name ('Polly.ResiliencePipeline' or 'ResiliencePipeline') also works.")]
        string[] identifiers,
        [Description(EcosystemHelp)]
        string? ecosystem = null,
        [Description("Optional: comma separated sections. sig = signatures and constructors, methods, props, data = enum values, doc, rel = related type ids, or all. " +
            "Default: a type gets sig, methods, props, doc, rel; a method gets sig, doc, rel; an enum gets data, doc. Unknown names are ignored. Ask for one section of a large type, e.g. 'methods'.")]
        string? sections = null,
        [Description("Optional: exact package version, from the project's package list or lock file. Omit for the latest stable.")]
        string? version = null,
        [Description("Optional, NuGet only: target framework such as 'net10.0' (default: the configured default)")]
        string? targetFramework = null,
        [Description("Optional: allow prerelease when no version is given (default false; NuGet only)")]
        bool includePrerelease = false,
        [Description(WorkspaceHelp)]
        string? workspace = null,
        [Description(ProjectHelp)]
        string? project = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(packageId, version, async () => await symbols.ExpandAsync(
            await RequestAsync(packageId, ecosystem, version, targetFramework, includePrerelease, null, workspace, project, cancellationToken).ConfigureAwait(false),
            identifiers ?? [],
            sections,
            cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// The request for the lookup. A PyPI or npm request has no framework; when it names a workspace and no version, the version is the one the project's lock file pins.
    /// </summary>
    private async Task<PackageIndexRequest> RequestAsync(
        string packageId,
        string? ecosystem,
        string? version,
        string? targetFramework,
        bool includePrerelease,
        string[]? additionalPackageIds,
        string? workspace,
        string? project,
        CancellationToken ct)
    {
        var kind = PackageEcosystems.Parse(ecosystem);
        if (kind == PackageEcosystems.NuGet)
        {
            return new PackageIndexRequest(packageId, version, targetFramework, includePrerelease, additionalPackageIds);
        }

        var pinned = string.IsNullOrWhiteSpace(version) ? await pins.FindAsync(kind, workspace, project, packageId, ct).ConfigureAwait(false) : version;
        return new PackageIndexRequest(packageId, pinned, PackageEcosystems.TargetOf(kind), includePrerelease, null, kind);
    }

    private async Task<string> RunAsync(string? packageId, string? version, Func<Task<string>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (MissingSiblingAssembliesException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (NuGetLoadException ex)
        {
            var subject = packageId is null ? string.Empty : $"'{packageId}' version '{version ?? "latest"}'";
            throw new McpException(
                $"Could not resolve {subject}: {ex.Message} Check the package id (nuget.org, pypi.org or npmjs.com), or use `pkg_search` to look it up.");
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            throw new McpException(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Package lookup for {PackageId} failed unexpectedly", packageId);
            throw new McpException(
                $"Indexing '{packageId}' failed unexpectedly: {ex.Message} Try `pkg_outline(refresh: true)`, or pin `version`.");
        }
    }
}
