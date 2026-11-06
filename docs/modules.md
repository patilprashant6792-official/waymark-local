# Modules

Waymark is a modular monolith. A module references `Waymark.Core` only; `Waymark.Data` is the only project that touches SQLite; `Waymark.Host` wires everything and serves the admin UI and the MCP endpoint.

| Module | Tools | Gate |
|---|---|---|
| Code | code_skeleton, code_folder_files, code_changes_since, code_outline, code_method, code_read, code_search, code_grep, code_callers, code_types (C# through Roslyn; Python and TypeScript/JavaScript through tree-sitter in `Waymark.Languages.Python` and `Waymark.Languages.TypeScript`; a language is an `ILanguageIndexer`) | Read |
| Code (feature maps) | recall, memorise | Read |
| Packages (project `Waymark.Modules.NuGet`, module `packages`) | pkg_search, pkg_outline, pkg_find, pkg_expand (`ecosystem`: nuget, pypi or npm) | Read |
| Files | file_write, file_edit_lines (both can copy with `from` / `fromFolder`), file_replace, folder_create, file_move, file_delete, folder_move, folder_delete | Confirm (off until switched on) |
| Commands | cmd_project (.NET and Python actions), cmd_git, cmd_proc, cmd_shell, cmd_wait, cmd_output, cmd_list, cmd_stop | Confirm for the four that start something (off until switched on); the read tools are Read. Each operation also has an Off / Ask / Auto gate chosen by the owner, see `commands-policy.md` |
| Host | waymark_ping, util_datetime | Read |

Not migrated on purpose: `http_request`, `list_environments`. `execute_dotnet_command` is replaced by the typed `cmd_project`.

`code_grep` finds literal text in any readable file (not only C#) by reading the files from disk on every call, so it is never stale. It applies each workspace's access rules, skips vendored folders (`wwwroot/lib`, `dist`, `vendor`), minified, binary and over-1 MB files, and reports how many it left out. Limits are set under `Waymark:Code:Grep:*` (see `ContentOptions`). There is no content index in the database; a SQLite full-text index is only worth adding if scans prove slow on real workspaces.

## Code index (watcher and indexer)

The watcher only queues what the operating system reports; one reader judges each event and one timer per workspace hands a quiet burst to the scheduler in a single request. A created, moved or deleted folder compares just that folder with the index, not the whole workspace. The indexer reads and parses files with several workers at once and stores the results in batches (one transaction per batch, each statement compiled once per batch, each file under its own savepoint, so one bad file never loses the batch). The "Indexed workspace" log line reports the time spent walking, reading and parsing, and writing. Settings are under `Waymark:Code:*` (`CodeIndexOptions`): `IndexParallelism` (default: processors minus one), `WriteBatchFiles` 200, `WriteBatchMs` 250, `WatcherDebounceMs` 300, `MaxPendingFiles` 2000.
## Workspace overview (`code_skeleton`)

The overview is a breadth-first tree under a size budget: `FolderScanner` reads the directory listings, `TreePlanner` decides which folders open, `FolderTreeRenderer` prints them, and closed folders get a one-line summary marked ▸. `path`, `depth` and `page` open folders on demand. The workspace's access rules apply to every walker. Settings are under `Waymark:Code:Skeleton:*` (`SkeletonOptions`): `OverviewBudgetChars` 12000, `MaxChildrenShown` 100, `MaxDepth` 64, `ScanBudgetSeconds` 20, `MaxProjectsInline` 25, `SolutionInlineChars` 2000, `LineCountReadBudgetBytes` 32 MB.

## Copying (`from`, `fromFolder`)

`file_edit_lines` and `file_write` can take text from a file instead of from the agent, so moving or migrating code costs no output tokens.

- `CopySourceReader` (Files) resolves the source through `IPathGuard` (any enabled workspace), reads each source once per call, checks `expectFirstLine` / `expectLastLine`, applies `replace` → `csharp` → `indent`, and asks `IContentInspector` whether the text holds a secret.
- `CopyResolver` turns `from` into plain content before `LinePatcher` runs, so the overlap and bottom-up rules are unchanged. A cut inside the same file becomes a delete patch; a cut from another file is committed by `CopyTransaction` (all files change or none, originals restored on failure).
- `FolderCopyPlanner` expands `fromFolder` (glob below the folder, `pathRename`, collision check, access rules per item) and `confirmCount` guards against a changed tree. `PathGlob` lives in `Waymark.Core/Text` and is shared with `code_grep`.
- C# renames go through `ICSharpRewriter` (`Waymark.Core/Code`); `CSharpRewriter` in the Code module uses Roslyn to find names and edits the text by position, so formatting and comments outside the renamed names are untouched. It works on syntax only, without symbol resolution.
- Settings under `Waymark:Files:*` (`FilesOptions`): `MaxWriteBytes` 2000000, `MaxBatchEntries` 50, `MaxPatchesPerCall` 200, `MaxFolderEntries` 20000, `MaxCopySourceBytes` 10000000, `MaxTransformsPerCopy` 50, `MaxCopyFilesPerCall` 500.

## Find and replace (`file_replace`)

One Confirm tool, two calls: a preview that reads and keeps a plan, and an apply that writes exactly that plan. Code in `Waymark.Modules.Files/Replace`.

- `ReplaceService` (facade, `IReplaceService`) checks the arguments (`ReplaceRequestValidator`), runs `ReplacePlanner` for a preview and `ReplaceApplier` under `FileWriteGate` for an apply.
- `ReplacePlanner` lists files through `ITextFileCatalog` (Core; `ContentFileCatalog` in the Code module wraps the `code_grep` walker, so the access rules, vendored and minified rules and the 1 MB limit are the same; the Files module registers `NoTextFileCatalog` as the empty default). It reads each file, finds the places (`LiteralMatcher`, or `ICSharpRewriter.FindIdentifier` in C# mode), hashes the bytes (SHA-256) and stops under its own time budget. A partial scan, a refused named path, too many files or matches, or no match gives no plan.
- `ReplacePlanStore` keeps plans in memory: random 128-bit id, `PlanMinutes` (10), single use, at most `MaxOpenPlans` (16). A restart forgets them. A plan holds the normalised arguments (`ValidatedRequest.Signature`), and per file its path, hash and places; no file text.
- `ReplaceApplier` reads every planned file again and refuses if any hash differs, builds every new file in memory (`SpanApplier`, then `TextFileFormat.EncodeExact`, which keeps every line ending), refuses a replacement that adds a secret on a changed line (`IContentInspector`, before and after), then writes through `CopyTransaction` with `CancellationToken.None`, so the call timeout cannot stop it halfway. One `FileChanged` per file; the tool asks `IFlowEditAdvisor` once with every path.
- Exclusions (`ReplaceExclusions`): `path` or `path#L12`, applied to the plan before the count check.
- Settings under `Waymark:Files:Replace:*` (`ReplaceOptions`): `MaxFiles` 200, `MaxMatches` 5000, `MaxFindChars` 200, `MaxWithChars` 1000, `PlanMinutes` 10, `MaxOpenPlans` 16, `ScanBudgetSeconds` 20, `MaxPreviewLinesPerFile` 20.
- `AtomicFile` swaps an existing file with `File.Replace` on Windows (keeps creation time, ACLs, attributes and named streams), falling back to a move; this applies to every file tool.
- Tests: `tests/Waymark.Tests` (xUnit v3 on Microsoft Testing Platform); the acceptance prompt is `docs/file-replace-test.md`.

## Feature maps = flows (`recall`, `memorise`)

Two tools of the Code module save and return the flows Claude traced: one trigger to one outcome as ordered method steps. A map is a name, a summary, a version and up to 30 steps (`path:Type.Member`; where no method exists, a file `path` or a range `path#L10-40`), each with its depth below the trigger (0 is the trigger), how it is reached from the step above (`call`, `http`, `event`, `ui`, `di`, `job`) and an optional note. It holds names only, never code, in the tables `feature_maps` and `feature_components` of `waymark.db` (migrations 017 and 018), one set per workspace, so a map is the same on every branch. A map saved before 018 is **unordered** (a plain list at depth 0); it keeps working and an agent orders it on next use.

The text form is one step per line, two spaces of indent per level meaning "called by the step above", an optional `~event` / `~http` / `~ui` / `~di` / `~job`, an optional `| note` (`FlowStepText`, Core, pure; the same grammar memorise, the bundle import and the page use).

- `recall`: `list` (query words or a method name, page) and `get` (ids). `get` never returns code: it returns each step's place, signature and the state of its link from the step above, so the agent reads only the steps it needs with `code_method`. Each step is checked against the disk at that moment, through the path guard (`FlowInspector`: `FlowStepReader` reads and parses the file, `FlowLinkChecker` compares the calls the parsed file makes with the step below, with the same receiver rule as `code_callers`, `FlowDriftFinder` suggests where a missing method moved). A step that is not in the checked-out code is listed as not on this branch and stays stored. Only a link between two steps that are both here can be reported as not holding.
- `memorise`: `save_feature_map` creates a flow (without `expectedVersion`) or replaces all its steps (with the version that was read; `newName` renames). New or changed steps must exist now and each new link must be a call the code makes; steps stored before stay whatever branch they came from. Creating a flow at the same trigger as another is refused unless `distinct` says how it differs. A stale version is refused with the current flow in the reply. `delete_feature_map` deletes one. Text that looks like a secret is refused. The guide tells the agent to delete one of two flows that describe the same trigger and outcome.
- After `file_edit_lines`, `file_write`, `file_move` or `file_delete` the result may carry a `🧭` line (`FlowEditAdvisor`, behind `IFlowEditAdvisor` in Core; the files module has a silent default and the code module replaces it): it speaks only when a link no longer holds, a step is gone from its file, or a file that held steps was moved or deleted. One indexed lookup by file path decides whether there is anything to say, it gives up after `Waymark:FeatureMaps:EditHintTimeoutSeconds` (2) and never fails the change. `Waymark:FeatureMaps:EditHints=false` switches it off.
- Search by meaning is not built: `IFlowSemanticIndex` (Core) has an empty registration (`NoFlowSemanticIndex`, every method documented with what it will do), `FlowSemanticText` composes what would be embedded, and `FlowSemanticSync` listens to `FlowChanged` (saved, deleted, imported) so every writer reaches the index through one place. Nothing changes while `IsEnabled` is false.
- The live setup of the overview lists the newest 15 flows of each workspace with id, step count and a one-line summary (60 characters), so an agent can pick one without listing first; the rest are one `recall list` away.
- Owner page: a workspace's **Feature maps** section (`/workspaces/{id}/features`) shows each flow as an indented tree with, per step, whether it is in the code that is checked out and what the link from the step above says (Calls it, By name only, Declared, Does not call, Not judged), checks them on load and on **Check against the code**, deletes a map and removes a step (not the trigger, and not a step that has steps under it). It reloads when Claude saves or deletes a map (`FeatureMapsChanged`), and every change is audited.
- Code: `FlowStepReader`, `FlowLinkChecker`, `FlowDriftFinder`, `FlowInspector`, `FlowRecallRenderer`, `FlowRecallService`, `FlowSaveService`, `FlowEditAdvisor`, `FeatureComponentChecker` (the checker the pages use) and the thin `FeatureMemoryService` in `Waymark.Modules.Code/Memory`; `FlowStepText`, `FeatureMapRules` and the contracts in `Waymark.Core/Memory`; `FeatureMapStore` in `Waymark.Data`; `FeatureMapAdminService` in `Waymark.Host/FeatureMaps`. Settings: `Waymark:FeatureMaps:*` above; the limits and the name rule are in `FeatureMapRules` (Core). Every call is in the audit log, which is also how to see whether agents use the tools.
- Keeping flows is part of the agent's task, not an option: the server instructions, the overview (`Waymark.Host/Guide.md`), the code guide, the files guide and the `recall` / `memorise` descriptions all say the same loop: find, read only the steps needed, change, then save only when the path changed (update the flow of the same trigger and outcome, create one for a new trigger, delete one of two that say the same).

## Workspace bundle (export and import)

Moves the workspaces, their access rules and their feature maps to another computer. Owner-only: the buttons are in the admin UI (`BundleToolbar` on the Workspaces page and on a workspace's Access rules and Feature maps sections); no MCP tool exports or imports, so Claude can neither read the rules out nor widen them.

- File: one JSON document, `waymark-workspace-bundle` version 2 (`Waymark.Core/Transfer`); version 1 files are still read (their maps import as unordered lists). Names, patterns and step paths only, with the depth and link of each step. No ids of this computer, no secrets, no vault, tokens, command gates or index. The folder a workspace had is written as a hint only.
- Import (`WorkspaceBundleService`): the text is untrusted. It is checked for secrets, then cleaned by `WorkspaceBundleSanitizer` with the same rules the UI and `memorise` apply (a flow is checked with the `memorise` step grammar); whatever fails is left out with a note. The owner picks per workspace: merge into one that exists, create it in a folder of this computer, or skip. `BundleMergePlanner` (pure) works out the preview and the import alike. It only adds: a rule that exists stays as it is, and a map is never merged: a map of the same name with the same steps is counted as here already, with other steps it is left out and counted, so the map in this workspace stays as it is. Rules that allow more file types are flagged in the preview.
- Write: `WorkspaceBundleStore` stores every plan in one transaction, so a failed import writes no rules or maps. A workspace created just before stays; running the same file again adds nothing twice.
- Audit: `config.bundle_exported`, `config.bundle_imported`. Events: `AccessRulesChanged`, `FeatureMapsChanged`, `FlowChanged` (imported), `WorkspaceChanged`.
## Adding a tool

1. Put the logic behind an interface in the module; register it in the module's `AddServices`.
2. Add a method with `[McpServerTool(Name = "...")]` to a tool class; add `[ToolGate(ToolGate.Confirm)]` if it changes anything.
3. Throw `McpException` for expected failures; use `OutputBudget` to bound output.
4. The registry, Tools page, pause switch, timeout and audit pick it up from the attribute. No other wiring.
5. A module with tools also ships a `Guide.md` (the usage text agents ask for), embedded as `Waymark.Guides.<module>.md` in its `.csproj`. Start-up fails without it. See `guides.md`.

## Data

Migrations are numbered files in `Waymark.Data/Migrations` (next: 019; 013 rebuilt the code index with a language column, 014 renamed the package and project tools, 017 added the feature maps, 018 made them flows: version, ordered, depth, link kind, path). Writes go through `IWriteQueue`, reads through `ISqliteConnectionFactory`.

## Vault

Named secrets, encrypted with ASP.NET Data Protection (keys next to the database, DPAPI on Windows). The UI never shows a value. `VaultRules.Scrub` replaces secret values in text with `[secret:NAME]`. It is **not yet applied** to command output (open item, see `commands-policy.md`).

The Vault page is hidden from the admin menu and the command palette for now. The page, store and rules are kept for further implementation.

## Commands

Typed recipes (`cmd_project` for .NET and Python, `cmd_git`, `cmd_proc`) run without a shell; `cmd_shell` runs any script and always waits for the owner. The owner decides in the admin UI on the same computer: `/console` (live terminal, a tab per workspace plus System, approval cards) and `/commands` (Off / Ask / Auto per operation, trusted project files, shells, limits). Contracts live in `Waymark.Core/Commands`; the engine in `Waymark.Modules.Commands`; the pages in `Waymark.Host`. Rules, defaults and settings: `commands-policy.md`.
