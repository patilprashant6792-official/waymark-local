# Language support: TypeScript / JavaScript (Node)

Branch: `feature/node-typescript`. Status: implemented and tested on a TypeScript sample (outline, callers, search, skeleton pins, `node_*` commands, npm package lookup). Still open: reading `node_modules` instead of downloading, pnpm and yarn installs, Vue and Svelte parsing, the xUnit test project. Decisions D1 and D2 were taken as recommended (`pkg_*`, `cmd_project`); D4: npm installs first.
Sibling plans: [python.md](python.md). This file also defines the shared groundwork (language registry, migration 013, `pkg_*` / `cmd_project`) that every language plan builds on.

## Goal

One Waymark session works on a .NET backend and a JS/TS frontend at the same time, with the same tool count (32). Language is dispatched from the file or project a tool is given, never from the tool name.

## Decisions still open (owner)

| # | Decision | Options | Recommended |
|---|---|---|---|
| D1 | Package tools | A: rename `nuget_*` to `pkg_*` + `ecosystem`/`project` params (module `packages`). B: keep `nuget_*` names, add the params. | A, but as its own phase (it touches guides, tool switches, UI routes) |
| D2 | Command tool | A: rename `cmd_dotnet` to `cmd_project`, toolchain from the target manifest. B: keep `cmd_dotnet`, add a `node` family behind it. | A |
| D3 | Languages in v1 | TS/TSX/JS/JSX (+ .mts/.cts/.mjs/.cjs). Vue/Svelte later (no grammar bundled in TreeSitter.DotNet). | as stated |
| D4 | Package managers in v1 | npm (package-lock.json) first; pnpm second; yarn last. | as stated |
| D5 | Tests | `tests/Waymark.Tests` is empty. Create an xUnit project for the new parsers and readers? | yes |

## Facts the plan rests on

- C# is assumed in exactly these places: `CodeFileWalker` (`*.cs`), `FileWatcherHost` (lines 112, 232), `WorkspaceIndexer` (line 123), `CodeReadService.ParseAsync` (line 189), `CodeCallersService` (line 51), `SyntaxIndexer` (Roslyn), `OutlineRenderer`/`MethodRenderer` (```csharp fences, "C# File Analysis"), `FolderScan.ProjectExtension = ".csproj"`, `ProjectFileSummarizer` (XML only), `WorkspaceScanner` (`*.csproj`).
- SQLite `code_types.kind` and `code_members.kind` have CHECK constraints listing C# kinds only; STRICT tables cannot alter a CHECK, so the tables are recreated. The code index is a copy of the disk (migration 010 already wiped it), so recreating is safe.
- `nuget_packages` is keyed `(package_id, version, tfm)` case-insensitively; `Polly` (NuGet) and `polly` (npm) would collide, so an ecosystem column is needed.
- The module name comes from the assembly name (`ToolRegistry.ModuleName`: `Waymark.Modules.NuGet` -> `nuget`). Guide rows (`tool_guides`, `tool_guide_deliveries`) and tool switches (`tool_settings`) are keyed by module and tool name, so a rename needs a data migration.
- `node_modules` is a blocked folder for agent tools (`FileAccessPolicy`). The npm module reads it internally, the same way NuGet reads the global packages folder; agent tools stay blocked.
- `DiagnosticsParser.LocatedPattern` already matches `tsc --pretty false` output (`file(line,col): error TS2322: ...`); `RunReport` only parses it for `dotnet.*` operations (line 98).
- `ExecutableLocator` appends `.exe` on Windows. npm/pnpm/yarn are `.cmd` shims there; starting a `.cmd` with arguments goes through cmd.exe parsing (BatBadBut class of injection). Run `node.exe <npm-cli.js>` instead.
- `ProcRecipes.stop_port` only ends programs inside the workspace or `dotnet` hosts; a Vite/Next dev server is `node.exe` from Program Files, so it is refused today.
- `FileAccessPolicy` blocks any path containing `token`, `secret`, `private`... Frontends often have `useToken.ts`, `tokens.css`. Pre-existing behaviour, not changed by this plan; workspace access rules can allow a specific path.
- TreeSitter.DotNet 1.3.0 (MIT, netstandard2.0, native libs for win/linux/mac) confirmed via nuget tools: `Language(string id)`, `Parser(Language)`, `Parser.Parse(string) -> Tree`, `Query(Language, string)`, `Query.Execute(Node) -> QueryCursor`, `QueryCursor.Captures/Matches`, `Node.StartPosition/EndPosition/Text/Type/GetChildForField`. All IDisposable. Exact grammar ids for TypeScript/TSX: to confirm in the spike.

## Layer by layer

### 1. Core contracts (`src/Waymark.Core`)

| File | Change |
|---|---|
| `Code/CodeIndexModels.cs` | add `enum CodeLanguage { CSharp, TypeScript, JavaScript }`; `CodeFileIndex` gets `CodeLanguage Language = CodeLanguage.CSharp` as last parameter; `CodeTypeKind` += `TypeAlias`, `Namespace`; `CodeMemberKind` += `Function`, `Variable`; `CodeIndexStats` += files per language |
| `Code/ILanguageIndexer.cs` (new) | moved from `Modules.Code/ISyntaxIndexer.cs`: `SyntaxIndexOutcome`, `SyntaxIndexResult`, `ILanguageIndexer { CodeLanguage Language; IReadOnlySet<string> Extensions; Task<SyntaxIndexResult> IndexFileAsync(...) }` |
| `Code/CodeLanguages.cs` (new) | one extension -> language map (`.cs`; `.ts .tsx .mts .cts`; `.js .jsx .mjs .cjs`), `IsIndexable(path)`, fence name, display name |
| `Code/CodeFileClassification.cs` | += `component`, `declarations` (.d.ts), `test`, `config` |
| `Code/CodeSearchModels.cs` | `CodeSymbolQuery` += optional `IReadOnlySet<CodeLanguage>? Languages` |
| `Security/FileAccessPolicy.cs` | allowed extensions += `.mjs .cjs .mts .cts` (later `.vue .svelte .scss`); blocked names += `.npmrc`, `.yarnrc`, `.yarnrc.yml` |
| `Workspaces/WorkspaceModels.cs` | `WorkspaceMetadata` += `NodeProjects` (package.json paths) and `PackageManager` |
| `Workspaces/WorkspaceScanner.cs` | also collect `package.json` (skip `node_modules`), detect package manager from lockfile / `packageManager` field |
| `Workspaces/WorkspacePathRules.cs` | `ExcludedDirectories` += `.next`, `.nuxt`, `.svelte-kit`, `.turbo`, `coverage` |
| `Workspaces/WorkspaceService.cs` | line 29 "no projects" check counts Node projects |
| `Commands/CommandCatalog.cs` | `NodeFamily = "node"`: `node.info` Auto, `node.list` Auto, `node.typecheck` Auto (drift), `node.install` Ask, `node.build` Ask, `node.test` Ask, `node.lint` Ask, `node.script` Ask (MaxGate Ask), `node.run` Ask, `node.package_add` Ask, `node.package_remove` Ask |
| `Commands/RiskTiers.cs` | network: `node.install`, `node.package_add`; project code: `node.build/test/lint/script/run`; RunsCode rule covers the node family |
| `NuGetIndex/*` (D1=A: rename folder to `Packages/`) | key gets `Ecosystem` (`nuget`/`npm`); symbol kind += `Function`, `TypeAlias`, `Namespace`, `Variable` |
| `Guides/GuideRules.cs` | `ServerInstructions`: "packages (NuGet and npm APIs)" |

### 2. SQLite (`src/Waymark.Data`)

| File | Change |
|---|---|
| `Migrations/013_polyglot_code_index.sql` (new) | drop triggers, `code_fts`, `code_call_sites`, `code_members`, `code_types`, `code_files`; recreate with `code_files.language TEXT NOT NULL DEFAULT 'csharp' CHECK (language IN ('csharp','typescript','javascript'))` and the extended kind CHECKs; recreate indexes, FTS and triggers. The start-up reconcile refills it |
| `Migrations/014_packages_ecosystem.sql` (new, D1) | `package_index` / `package_symbols` / `package_namespaces` / `package_fts` / `package_metadata_cache` with `ecosystem` in the key; drop `nuget_*` (cache, refilled on demand); `UPDATE tool_settings` and `tool_guides`/`tool_guide_deliveries` for renamed tools and module |
| `Stores/CodeIndexStore.cs` | write/read `language`; stats grouped by language |
| `Stores/CodeSearchStore.cs` | optional language filter in `BuildSql` |
| `Stores/NuGetIndexStore.cs`, `NuGetCacheStore.cs`, `NuGetMetadataStore.cs` | ecosystem in every key, query and row (renamed `Package*Store` under D1) |
| `DataServiceCollectionExtensions.cs` | registrations follow the renames |

### 3. TypeScript language project (new `src/Waymark.Languages.TypeScript`)

Not named `Waymark.Modules.*`, so `ToolRegistry` does not treat it as a module. Referenced by the Code module and the Packages module. Native code stays out of `Waymark.Modules.Code`.

| File | Purpose |
|---|---|
| `Waymark.Languages.TypeScript.csproj` | references Core + `TreeSitter.DotNet` (add `PackageVersion` 1.3.0 to `Directory.Packages.props`, project to `Waymark.slnx`) |
| `TypeScriptIndexer.cs` | `ILanguageIndexer` for TS and JS: hash short-circuit and size limit like `SyntaxIndexer`; grammar per extension (tsx for .tsx/.jsx) |
| `Grammars.cs` | lazy singleton `Language` objects; a new `Parser` per call (parsers are not thread-safe; the index and `code_outline` run concurrently) |
| `Queries/*.scm` (embedded) | declarations, members, imports, calls |
| `TsSyntaxExtractor.cs` | tree -> `CodeTypeEntry`/`CodeMemberEntry`/`CodeCallSiteEntry`: class, interface, enum, type alias, namespace; method, constructor, property, field, enum member; top-level function and exported arrow const -> `Function`; other exported const/let -> `Variable`; `extends`/`implements` normalised to a comma list so `BaseListParser` works; decorators -> `Attributes`; JSDoc -> `DocSummary`; imports -> `Usings`; honours `MaxMembersPerFile` / `MaxCallSitesPerFile` |
| `TsFileClassifier.cs` | component (returns JSX), declarations, test (`*.test.*`, `*.spec.*`, `__tests__`), config (`*.config.*`), interfaces, source |
| `DeclarationReader.cs` | reads `.d.ts` files of a package (public API for the Packages module) |

### 4. Code module (`src/Waymark.Modules.Code`)

| File | Change |
|---|---|
| `ISyntaxIndexer.cs`, `SyntaxIndexer.cs` | become `CSharpIndexer : ILanguageIndexer` (Roslyn code unchanged) |
| `LanguageIndexers.cs` (new) | picks the indexer for a path; used everywhere below |
| `CodeModule.cs` | register `CSharpIndexer` and `TypeScriptIndexer` as `ILanguageIndexer` |
| `CodeIndexOptions.cs` | += `IndexExcludedFolders` (default `dist`, `build`, `out`, `.next`, `coverage`), configurable |
| `Scheduling/CodeFileWalker.cs` | enumerate all files, keep those the registry indexes, skip excluded folders |
| `Scheduling/FileWatcherHost.cs` (112, 232) | `.cs` checks -> registry |
| `Scheduling/WorkspaceIndexer.cs` (55, 123) | registry check, messages say "source files" |
| `Reading/CodeReadService.cs` | `ParseAsync` uses the registry; `MethodsAsync` also matches top-level members (`TypeIndex == null`); batch header per language |
| `Reading/OutlineRenderer.cs`, `MethodRenderer.cs` | fence and title from the file language; "Imports" for TS |
| `Search/SearchKinds.cs` | += `Function`, `TypeAlias`, `Namespace`, `Variable` |
| `Search/CodeCallersService.cs` | registry check (51); `FindTarget` accepts `Function`; `FilterOffReason` covers top-level functions |
| `Search/CodeSearchService.cs`, `CodeTypesService.cs` | no logic change; descriptions only |
| `Skeleton/FolderScan.cs` | project marker: `.csproj` or a file named `package.json` |
| `Skeleton/ProjectFileSummarizer.cs` | split: `CsprojSummarizer` (existing code) + `NodeProjectSummarizer` (name, packageManager, engines, TypeScript version, dependencies pinned from the lockfile, workspaces) |
| `Skeleton/SkeletonService.cs` | composes both summaries; file-not-folder message neutral |
| `Content/ContentOptions.cs` | vendored folders += `.next`, `coverage` |
| `Tools/*.cs` | descriptions language-neutral ("source file(s)", examples with .ts) |
| `Guide.md` | TS rows, `.d.ts`, kinds, JSON camelCase note for cross-stack search |

### 5. Files module

| File | Change |
|---|---|
| `CopySourceReader.cs` | refuse the `csharp` transform for files that are not `.cs` (today it would parse TS as C# and rename by position) |
| `Tools/FileEditLinesTool.cs`, `FileWriteTool.cs`, `Guide.md` | say the `csharp` transform is C# only |

### 6. Packages module (`src/Waymark.Modules.NuGet` -> `Waymark.Modules.Packages` under D1)

| Area | Change |
|---|---|
| `Tools/NuGetTools.cs` | `pkg_search`, `pkg_outline`, `pkg_find`, `pkg_expand`; new params `ecosystem` (nuget, npm) and `project` (a `.csproj` or `package.json`; gives ecosystem, pinned version and target) |
| `IPackageEcosystem` (new) | `ResolveAsync(request) -> resolved version + target`, `LoadSymbolsAsync(...)`; NuGet implementation wraps today's `PackageLoader`/`AssemblyReflector`/`SymbolDocumentFactory` |
| `Npm/LockfileReader.cs` | package-lock.json v2/v3 (`packages["node_modules/<name>"].version/integrity`); pnpm-lock.yaml next (needs a YAML reader: add YamlDotNet or a minimal reader; decide then) |
| `Npm/NpmRegistryClient.cs` | public registry only (packument, `/-/v1/search`, tarball); timeout, retry with backoff, size limit; private registries out of scope (their tokens live in `.npmrc`, which Waymark must not read) |
| `Npm/TarballReader.cs` | `GZipStream` + `System.Formats.Tar`; extracts only `package.json` and `*.d.ts`; refuses `..`, links and oversize entries; verifies `dist.integrity` (sha512 SRI) against the lockfile |
| `Npm/LocalPackageSource.cs` | prefers `node_modules/<name>` when its `package.json` version equals the lockfile pin (no network) |
| `Npm/TypesEntryResolver.cs` | `exports` `types` condition, then `typesVersions`, `types`/`typings`, `index.d.ts`, then `@types/<name>` (`@scope/pkg` -> `@types/scope__pkg`); none -> "no declared shape" |
| `Npm/NpmSymbolFactory.cs` | `DeclarationReader` output -> symbol entries with the same sections (sig, methods, props, data, doc, rel); follows relative `export * from` inside the package; external re-exports marked |
| `NuGetLoadOptions.cs` | += npm cache folder (`%LOCALAPPDATA%\Waymark\npm`), registry URL, limits |
| `Guide.md` | one flow for both ecosystems; shape/behaviour rule unchanged |

### 7. Commands module

| File | Change |
|---|---|
| `Recipes/NodeRecipes.cs` (new) | info, list, install (`npm ci --ignore-scripts --no-audit --no-fund`), typecheck (`tsc --noEmit --pretty false` from local node_modules), build/test/lint (named scripts), script (named script only, body shown on the approval card), run (dev server, stays running), package_add (`--save-exact --ignore-scripts`), package_remove |
| `Recipes/RecipeContext.cs` | `ExistingNodeProject(key)` (package.json), manifest reader for scripts |
| `Recipes/RecipeRules.cs` | npm package name (incl. scope) and exact version validation, script name allow-list from package.json |
| `Tools/CommandRunTools.cs` | D2=A: `cmd_project` with the union of actions, toolchain from the target; D2=B: node actions in their own description block |
| `CommandService.cs` | dispatch by target manifest |
| `Execution/ExecutableLocator.cs` | find `node` (PATH, Program Files\nodejs, `NVM_SYMLINK`) and npm-cli.js beside it; never run `.cmd` shims |
| `Execution/ChildEnvironment.cs` | forced `CI=1` (no watch mode), `npm_config_update_notifier=false`, `npm_config_fund=false`; `NODE_OPTIONS` denied; tokens never pass (allow-list already) |
| `Execution/BuildFingerprint.cs` | += package.json, lockfiles, pnpm-workspace.yaml, .npmrc, .yarnrc.yml, .nvmrc, `*.config.{js,cjs,mjs,ts}`; skip `.next`, `dist` |
| `Execution/DiagnosticsParser.cs` | vitest and jest totals; tsc already matches |
| `Reporting/RunReport.cs` (98) | parse diagnostics for `node.*` too |
| `Recipes/ProcRecipes.cs` | `stop_port`: also end `node` processes whose command line points inside the workspace (needs the process command line on Windows; spike) |
| `Guide.md` | node section, rules for install and scripts |

### 8. Host and UI (`src/Waymark.Host`)

| File | Change |
|---|---|
| `Program.cs` | module list follows D1 |
| `Guide.md` | group table (packages, commands) |
| `Components/Pages/Workspaces.razor(.cs)` | Node projects, package manager, files per language |
| `Components/Pages/Home.razor(.cs)` | code card per language; packages card with ecosystem (keep card key `nuget` in `DashboardLayout`, saved layouts use it) |
| `Components/Pages/NuGetCache.razor(.cs)` | Packages page: route `/packages` (keep `/nuget`), ecosystem column and filter, cache folder text per ecosystem |
| `Components/Pages/NuGetExplorerPage.razor(.cs)` | route with ecosystem; scoped npm names contain `/`, so pass id in the query string, not a path segment |
| `NuGetAdmin/*` | ecosystem-aware; `NuGetLinks.OnNuGetOrg` -> registry link per ecosystem (npmjs.com) |
| `Components/Pages/CommandSettings.razor(.cs)` | `Group(NodeFamily, "Node.js")`; build-file text mentions package.json and lockfiles |
| `Components/Pages/RiskGuide.razor`, `Guides.razor`, `Connectors.razor` | npm tokens, lifecycle scripts, group names |
| `Components/Layout/MainLayout.razor`, `Components/Shared/CommandPalette.razor` | "Packages" nav entry |

### 9. Docs and install

- `README.md`, `docs/modules.md` (also fix its stale "next migration: 012"), `docs/commands-policy.md`, `docs/safety.md`, `docs/install.md` (Node optional), `docs/guides.md`.
- `scripts/waymark.ps1`: publish is portable (no RID), so native libs for every platform ship; measure size, consider `-r win-x64`.

### 10. Tests (D5)

New `tests/Waymark.Tests` (xUnit): TS extractor on fixture files (classes, interfaces, arrow components, overloads, decorators, broken syntax), language registry, lockfile reader, types entry resolver, tarball reader (path traversal, oversize, integrity mismatch), node recipes argument lists, diagnostics parser on tsc/vitest/jest output, migration 013 on a copy of a real database.

## Phases

Each phase ends with: clean `rebuild`, the owner's reinstall task, owner confirms the install.

| Phase | Scope | Done when |
|---|---|---|
| 0 Spike | throwaway console: TreeSitter.DotNet parses a real .tsx, grammar ids confirmed, native load OK on the install path | outline printed for one real file |
| 1 TS code index | sections 1 (code rows), 2 (013), 3, 4, 5 | `code_outline`/`code_method`/`code_search`/`code_types`/`code_callers` work on .ts/.tsx; C# output unchanged on this repo |
| 2 Workspaces and skeleton | workspace metadata, Node summarizer, access policy, UI Workspaces page | skeleton of a Node project shows lockfile pins |
| 3 Commands | section 7, D2 | typecheck/test/build on a real frontend; install proven not to run lifecycle scripts |
| 4 Packages | sections 2 (014), 6, D1 | `pkg_expand` correct for a typed, an `@types`-only and an untyped package |
| 5 UI and docs | sections 8, 9 | pages show both ecosystems; guides updated |

## Risks

- TreeSitter.DotNet has one maintainer and no release since Jan 2026: kept behind `ILanguageIndexer` in its own project.
- A native grammar crash ends the process: size limit, fixture tests on minified and generated files.
- Renames (D1, D2) break agent prompts and the owner's preferences that name `nuget_*` / `cmd_dotnet`.
- Dropping the NuGet cache in 014 makes the first lookup of each package slow again.
- The build fingerprint grows, so the first command after the upgrade asks the owner once.
