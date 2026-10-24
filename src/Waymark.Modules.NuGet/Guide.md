# Waymark NuGet tools

They show what a package exposes, read from the compiled package at the version the project uses. Use them instead of memory: signatures change between versions.

## Flow

1. Package id and version: the NuGet section of `code_skeleton(ws)`; with many projects it is a summary, so use `code_skeleton(ws, path=<project folder>)` for that project's pins. Id unknown: `nuget_packages(query)`.
2. `nuget_find(packageId, query)` returns ranked symbols with ids. An empty query browses the most prominent ones. Filters: `kind`, `namespace` (exact), `declaringType` (lists that type's methods).
3. `nuget_expand(packageId, identifiers)` returns exact constructors, overloads, properties, enum values, XML docs and related type ids. Up to 5 ids per call; pass related ids straight back. `sections`: sig, methods, props, data, doc, rel, all.
4. `nuget_outline(packageId)` returns the namespace map only. Use it for a completely unfamiliar package, or with `refresh: true` after a version bump.

Pin `version` (from the skeleton) and `targetFramework` (the project's, e.g. net10.0) in find, expand and outline, so the API matches what the project compiles against. Without a version you get the latest stable.

## Rules

- Shape is settled by `nuget_expand`: a member it did not confirm at the pinned version does not exist, so do not write the call.
- Behavior is not in the binary: call order, what a parameter is meant for, pitfalls. When the XML doc is thin and the call is not trivial, read the package README, samples or issues (web search, if you have it) after the signature is confirmed. Prose never overrides a confirmed signature.
- The first call for a package version downloads and indexes it, which takes a few seconds; later calls are instant.
- `additionalPackageIds` only after an error that names a missing assembly.
- Adding a package: `cmd_dotnet` action `package_add` (commands guide). Pick the latest stable that fits the project's target framework, and prefer the version other projects in the workspace already pin.
- Copied or migrated code brings its package usage with it. Check the packages the target project pins (`code_skeleton(ws, path=<project folder>)`) and confirm the API of the pinned version with `nuget_expand` before adding or upgrading a package.

## Errors

| Message | Action |
|---|---|
| Could not resolve the package or version | check the id with `nuget_packages`, and the pinned version in the skeleton |
| Missing assembly | retry with `additionalPackageIds` naming it |
| Unexpected failure | `nuget_outline(refresh: true)`, or pin `version` and `targetFramework` |
