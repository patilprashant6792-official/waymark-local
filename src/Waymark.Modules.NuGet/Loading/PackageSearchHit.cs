namespace Waymark.Modules.NuGet.Loading;

/// <summary>One result of a package search on the feed.</summary>
internal sealed record PackageSearchHit(string Id, string Version, long? Downloads, string Description);
