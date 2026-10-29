# Waymark package tools

They show what a package exposes, read from the package at the version the project uses. Use them instead of memory: signatures change between versions. One set of tools serves three ecosystems: `ecosystem` is `nuget` (default, .NET), `pypi` (Python) or `npm` (JavaScript and TypeScript).

## Flow

1. Package id and version: the package part of `code_skeleton(ws)` (NuGet packages with their target framework; Python and Node dependencies with the version their lock file pins); with many projects it is a summary, so use `code_skeleton(ws, path=<project folder>)` for that project's pins. Id unknown: `pkg_search(query)`.
2. `pkg_find(packageId, query)` returns ranked symbols with ids. An empty query browses the most prominent ones. Filters: `kind`, `namespace` (exact), `declaringType` (lists that type's methods).
3. `pkg_expand(packageId, identifiers)` returns exact constructors, overloads, properties, enum values, docs and related ids. Up to 5 ids per call; pass related ids straight back. `sections`: sig, methods, props, data, doc, rel, all.
4. `pkg_outline(packageId)` returns the map only (namespaces for NuGet, modules for PyPI and npm). Use it for a completely unfamiliar package, or with `refresh: true` after a version bump.

Pin `version` (from the skeleton) in find, expand and outline, so the API matches what the project runs against. NuGet also takes `targetFramework` (the project's, e.g. net10.0). Without a version you get the latest stable. For PyPI and npm, `workspace` (and `project`) takes the version from the project's lock file.

## Python packages (`ecosystem: "pypi"`)

- Pass `workspace` (and `project`, the folder of the Python project) and leave `version` out: the version is the one the project's lock file (uv.lock, poetry.lock, pdm.lock, pylock.toml) pins. A package that is not in the lock file is refused, because the project does not install it.
- PyPI has no search. `pkg_search` tries the query as project names (`requests`, `scikit learn` becomes `scikit-learn`); give the name as in `pip install`.
- The wheel's `.py` files are read by syntax: nothing is imported or run. A package that ships no wheel is read from its source archive, which is only unpacked, never built.
- Types come in PEP 561 order: the package's own `.pyi` files or `py.typed`; else the stub package (`types-NAME`, then `NAME-stubs`) at its latest version; else the plain `.py` source, where parameters carry no annotations.
- Only public names are indexed: no leading underscore, tests left out, and a module's `__all__` respected. A module is the namespace: `pkg_find(declaringType="requests.api")` lists that module's functions, `namespace="requests.sessions"` its classes.
- Methods are shown without `self`/`cls`. A dataclass or model with no `__init__` shows its annotated fields as the constructor.
- A package whose wheel is bigger than the owner's limit (`Waymark:Pypi:MaxArchiveBytes`, 150 MB by default) is refused.

## npm packages (`ecosystem: "npm"`)

- Pass `workspace` (and `project`, the folder of the package.json) and leave `version` out: the version is the one the project's lock file (package-lock.json, pnpm-lock.yaml, yarn.lock) pins. A package that is not in the lock file is refused. Package names are lower case, scoped ones keep their scope: `zod`, `@tanstack/react-query`.
- `pkg_search(ecosystem: "npm")` uses the registry's own search. The tarball is downloaded from the public registry, checked against the integrity hash the registry publishes, and only its `package.json` and declaration files are unpacked: nothing is installed, imported or run. Private registries are not read (their tokens live in `.npmrc`, which Waymark never opens).
- Shape comes in this order: the package's own `.d.ts` files; else the `@types/NAME` package (`@scope/pkg` is `@types/scope__pkg`) at its latest version; else the package's plain JavaScript, where parameters carry no types. The symbols reached first from the types entry (the `types` field, `exports`, `index.d.ts`) rank higher.
- A module is the path of a declaration file inside the package (`lib/types`); the root `index.d.ts` is named after the package. `declare module "fs"` and `namespace X` blocks are modules of their own: `pkg_find(declaringType="fs")` lists the functions of that block. A type alias is shown as a struct, a class or interface as itself, an exported constant as a function.
- Overloads are listed together; a method is shown as written (`on(event: string, handler: Handler): this`). Generic parameters are part of the signature.

## Rules

- Shape is settled by `pkg_expand`: a member it did not confirm at the pinned version does not exist, so do not write the call.
- Behavior is not in the binary or the signature: call order, what a parameter is meant for, pitfalls. When the doc is thin and the call is not trivial, read the package README, samples or issues (web search, if you have it) after the signature is confirmed. Prose never overrides a confirmed signature.
- The first call for a package version downloads and indexes it, which takes a few seconds; later calls are instant.
- `additionalPackageIds` (NuGet) only after an error that names a missing assembly.
- Adding a package: `cmd_project` action `package_add` for NuGet, `py_package_add` for Python, `node_package_add` for npm (commands guide). Pick the latest stable that fits the project, and prefer the version other projects in the workspace already pin.
- Copied or migrated code brings its package usage with it. Check the packages the target project pins (`code_skeleton(ws, path=<project folder>)`) and confirm the API of the pinned version with `pkg_expand` before adding or upgrading a package.

## Errors

| Message | Action |
|---|---|
| Could not resolve the package or version | check the id with `pkg_search`, and the pinned version in the skeleton |
| No lock file / not in the lock file (pypi, npm) | pass `version`, or check that the package is a dependency of that project |
| Missing assembly (NuGet) | retry with `additionalPackageIds` naming it |
| Unexpected failure | `pkg_outline(refresh: true)`, or pin `version` |
