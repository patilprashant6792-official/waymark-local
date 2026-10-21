using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Waymark.Modules.NuGet.Indexing;
using Waymark.Modules.NuGet.Loading;

namespace Waymark.Modules.NuGet.Tools;

[McpServerToolType]
public sealed class NuGetTools(INuGetSymbolService symbols, ILogger<NuGetTools> logger)
{
    [McpServerTool(Name = "nuget_packages", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Searches nuget.org for packages by name or keyword and returns id, latest version, download count and a one-line description. " +
        "Use it only when the exact package id is not known; skip it when the id is already in the project's package list. " +
        "Next step: nuget_find(packageId, query) to locate a type or method inside the package.")]
    public Task<string> Packages(
        [Description("Package name or keyword (e.g. 'openai', 'polly retry')")]
        string query,
        [Description("Optional: number of packages to return (default 5, max 8)")]
        int take = 5,
        [Description("Optional: include prerelease versions (default false)")]
        bool includePrerelease = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(null, null, () => symbols.PackagesAsync(query, take, includePrerelease, cancellationToken));

    [McpServerTool(Name = "nuget_outline", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Shows the namespace map of a package (namespace names with type counts), never a type listing. " +
        "Use it for a completely unfamiliar package, or with refresh=true after a version bump. Otherwise go straight to nuget_find. " +
        "The first call for a package version downloads and indexes it, which can take a few seconds; later calls are instant. " +
        "Pin the version and targetFramework of the project so the API matches what the project compiles against.")]
    public Task<string> Outline(
        [Description("NuGet package id (e.g. 'Polly')")]
        string packageId,
        [Description("Optional: only namespaces that start with this text")]
        string? @namespace = null,
        [Description("Optional: rebuild the index from the package instead of reading the stored one (default false)")]
        bool refresh = false,
        [Description("Optional: exact package version, from the project's package list. Omit for the latest stable.")]
        string? version = null,
        [Description("Optional: target framework such as 'net10.0' (default: the configured default)")]
        string? targetFramework = null,
        [Description("Optional: allow prerelease when no version is given (default false)")]
        bool includePrerelease = false,
        [Description("Optional: extra package ids whose assemblies the reflection needs, only after an error that names a missing assembly")]
        string[]? additionalPackageIds = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(packageId, version, () => symbols.OutlineAsync(
            new PackageIndexRequest(packageId, version, targetFramework, includePrerelease, additionalPackageIds), @namespace, refresh, cancellationToken));

    [McpServerTool(Name = "nuget_find", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Finds types and methods inside a package by describing what you need ('chat completion', 'retry policy'). Results are ranked best first and carry a symbol id. " +
        "An empty query browses the most prominent symbols. Filters: kind (class, interface, enum, struct, delegate, method), namespace (exact), declaringType (lists the methods of one type). " +
        "Pin version and targetFramework to the project's. Next step: nuget_expand with the ids to see exact signatures.")]
    public Task<string> Find(
        [Description("NuGet package id (e.g. 'Polly')")]
        string packageId,
        [Description("What you are looking for: a name, camel-case words or a description. Empty browses.")]
        string query = "",
        [Description("Optional: class, interface, enum, struct, delegate or method")]
        string? kind = null,
        [Description("Optional: exact namespace to search in")]
        string? @namespace = null,
        [Description("Optional: exact type name; lists that type's methods")]
        string? declaringType = null,
        [Description("Optional: results to return (default 10, max 25)")]
        int limit = 10,
        [Description("Optional: exact package version, from the project's package list. Omit for the latest stable.")]
        string? version = null,
        [Description("Optional: target framework such as 'net10.0' (default: the configured default)")]
        string? targetFramework = null,
        [Description("Optional: allow prerelease when no version is given (default false)")]
        bool includePrerelease = false,
        [Description("Optional: extra package ids whose assemblies the reflection needs, only after an error that names a missing assembly")]
        string[]? additionalPackageIds = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(packageId, version, () => symbols.FindAsync(
            new PackageIndexRequest(packageId, version, targetFramework, includePrerelease, additionalPackageIds),
            query, kind, @namespace, declaringType, limit, cancellationToken));

    [McpServerTool(Name = "nuget_expand", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Shows the exact shape of up to 5 symbols from nuget_find: constructors or overload signatures, methods, properties, enum values, XML documentation and related type ids. " +
        "The signatures come from the compiled package, so they are the source of truth for what exists. " +
        "Sections: sig, methods, props, data, doc, rel, or all (default depends on the kind of symbol). Pass the related ids straight back to expand them.")]
    public Task<string> Expand(
        [Description("NuGet package id (e.g. 'Polly')")]
        string packageId,
        [Description("Symbol ids from nuget_find or related ids (max 5); a full or trailing type name also works")]
        string[] identifiers,
        [Description("Optional: comma separated sections - sig, methods, props, data, doc, rel, or all")]
        string? sections = null,
        [Description("Optional: exact package version, from the project's package list. Omit for the latest stable.")]
        string? version = null,
        [Description("Optional: target framework such as 'net10.0' (default: the configured default)")]
        string? targetFramework = null,
        [Description("Optional: allow prerelease when no version is given (default false)")]
        bool includePrerelease = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(packageId, version, () => symbols.ExpandAsync(
            new PackageIndexRequest(packageId, version, targetFramework, includePrerelease), identifiers ?? [], sections, cancellationToken));

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
                $"Could not resolve {subject}: {ex.Message} Check the package id on nuget.org, or use `nuget_packages` to search.");
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            throw new McpException(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "NuGet lookup for {PackageId} failed unexpectedly", packageId);
            throw new McpException(
                $"Indexing '{packageId}' failed unexpectedly: {ex.Message} Try `nuget_outline(refresh: true)`, or pin `version` and `targetFramework`.");
        }
    }
}
