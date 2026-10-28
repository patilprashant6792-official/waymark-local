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
/// The four package lookups. One set of tools serves every ecosystem (NuGet and PyPI): the ecosystem is an argument, so the tool count stays the same
/// when a language is added.
/// </summary>
[McpServerToolType]
public sealed class PackageTools(INuGetSymbolService symbols, IPackagePins pins, ILogger<PackageTools> logger)
{
    private const string EcosystemHelp = "Optional: 'nuget' (default) or 'pypi'. Use pypi for a Python package.";
    private const string WorkspaceHelp = "Optional, pypi only: workspace name. With it, and no version, the version comes from the project's lock file (uv.lock, poetry.lock, pdm.lock, pylock.toml).";
    private const string ProjectHelp = "Optional, pypi only: the project folder (or its pyproject.toml) inside the workspace whose lock file decides the version; default the workspace folder.";

    [McpServerTool(Name = "pkg_search", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Looks a package up by name. NuGet (default): searches nuget.org and returns id, latest version, download count and a one-line description. " +
        "PyPI (ecosystem='pypi'): PyPI has no search, so the query is tried as project names (words joined with '-', then each word) and returns the projects that exist. " +
        "Use it only when the exact package id is not known; skip it when the id is already in the project's package list. " +
        "Next step: pkg_find(packageId, query) to locate a type, class or function inside the package.")]
    public Task<string> Search(
        [Description("Package name or keyword (e.g. 'openai', 'polly retry'; for pypi the project name, e.g. 'requests' or 'scikit learn')")]
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
        "Shows the map of a package (namespaces for NuGet, modules for PyPI, each with a count), never a listing of types. " +
        "Use it for a completely unfamiliar package, or with refresh=true after a version bump. Otherwise go straight to pkg_find. " +
        "The first call for a package version downloads and indexes it, which can take a few seconds; later calls are instant. " +
        "Pin the version (and for NuGet the targetFramework) of the project so the API matches what the project runs against. " +
        "PyPI packages are read from their wheel without running or importing anything.")]
    public Task<string> Outline(
        [Description("Package id (NuGet, e.g. 'Polly') or project name (PyPI, e.g. 'requests')")]
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
        "Finds types, classes, functions and methods inside a package by describing what you need ('chat completion', 'retry policy'). Results are ranked best first and carry a symbol id. " +
        "An empty query browses the most prominent symbols. Filters: kind (class, interface, enum, struct, delegate, method), namespace (exact; the module for PyPI), declaringType (lists the methods of one type; for PyPI a module name lists its functions). " +
        "Pin the version to the project's (a Python project's lock file gives it: pass workspace). Next step: pkg_expand with the ids to see exact signatures.")]
    public Task<string> Find(
        [Description("Package id (NuGet, e.g. 'Polly') or project name (PyPI, e.g. 'requests')")]
        string packageId,
        [Description("What you are looking for: a name, camel-case or snake_case words, or a description. Empty browses.")]
        string query = "",
        [Description(EcosystemHelp)]
        string? ecosystem = null,
        [Description("Optional: class, interface, enum, struct, delegate or method (a PyPI function is a method)")]
        string? kind = null,
        [Description("Optional: exact namespace to search in (the dotted module for PyPI)")]
        string? @namespace = null,
        [Description("Optional: exact type name; lists that type's methods (for PyPI a module name lists its functions)")]
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
        "Shows the exact shape of up to 5 symbols from pkg_find: constructors or overload signatures, methods, properties, enum values, documentation (XML comments for NuGet, docstrings for PyPI) and related type ids. " +
        "The signatures come from the package itself (the compiled assembly for NuGet; the wheel, with the stub package's types when the package has none, for PyPI), so they are the source of truth for what exists. " +
        "Sections: sig, methods, props, data, doc, rel, or all (default depends on the kind of symbol). Pass the related ids straight back to expand them.")]
    public Task<string> Expand(
        [Description("Package id (NuGet) or project name (PyPI)")]
        string packageId,
        [Description("Symbol ids from pkg_find or related ids (max 5); a full or trailing type name also works")]
        string[] identifiers,
        [Description(EcosystemHelp)]
        string? ecosystem = null,
        [Description("Optional: comma separated sections - sig, methods, props, data, doc, rel, or all")]
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
    /// The request for the lookup. A PyPI request has no framework; when it names a workspace and no version, the version is the one the project's lock file pins.
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
        if (PackageEcosystems.Parse(ecosystem) != PackageEcosystems.PyPI)
        {
            return new PackageIndexRequest(packageId, version, targetFramework, includePrerelease, additionalPackageIds);
        }

        var pinned = string.IsNullOrWhiteSpace(version) ? await pins.FindAsync(workspace, project, packageId, ct).ConfigureAwait(false) : version;
        return new PackageIndexRequest(packageId, pinned, PackageEcosystems.PyPITarget, includePrerelease, null, PackageEcosystems.PyPI);
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
                $"Could not resolve {subject}: {ex.Message} Check the package id (nuget.org or pypi.org), or use `pkg_search` to look it up.");
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
