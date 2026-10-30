# Modules

Waymark is a modular monolith. A module references `Waymark.Core` only; `Waymark.Data` is the only project that touches SQLite; `Waymark.Host` wires everything and serves the admin UI and the MCP endpoint.

| Module | Tools | Gate |
|---|---|---|
| Code | code_skeleton, code_folder_files, code_changes_since, code_outline, code_method, code_read, code_search, code_grep, code_callers, code_types (C# through Roslyn; Python and TypeScript/JavaScript through tree-sitter in `Waymark.Languages.Python` and `Waymark.Languages.TypeScript`; a language is an `ILanguageIndexer`) | Read |
| Packages (project `Waymark.Modules.NuGet`, module `packages`) | pkg_search, pkg_outline, pkg_find, pkg_expand (`ecosystem`: nuget, pypi or npm) | Read |
| Files | file_write, file_edit_lines (both can copy with `from` / `fromFolder`), folder_create, file_move, file_delete, folder_move, folder_delete | Confirm (off until switched on) |
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
## Adding a tool

1. Put the logic behind an interface in the module; register it in the module's `AddServices`.
2. Add a method with `[McpServerTool(Name = "...")]` to a tool class; add `[ToolGate(ToolGate.Confirm)]` if it changes anything.
3. Throw `McpException` for expected failures; use `OutputBudget` to bound output.
4. The registry, Tools page, pause switch, timeout and audit pick it up from the attribute. No other wiring.
5. A module with tools also ships a `Guide.md` (the usage text agents ask for), embedded as `Waymark.Guides.<module>.md` in its `.csproj`. Start-up fails without it. See `guides.md`.

## Data

Migrations are numbered files in `Waymark.Data/Migrations` (next: 015; 013 rebuilt the code index with a language column, 014 renamed the package and project tools). Writes go through `IWriteQueue`, reads through `ISqliteConnectionFactory`.

## Vault

Named secrets, encrypted with ASP.NET Data Protection (keys next to the database, DPAPI on Windows). The UI never shows a value. `VaultRules.Scrub` replaces secret values in text with `[secret:NAME]`. It is **not yet applied** to command output (open item, see `commands-policy.md`).

The Vault page is hidden from the admin menu and the command palette for now. The page, store and rules are kept for further implementation.

## Commands

Typed recipes (`cmd_project` for .NET and Python, `cmd_git`, `cmd_proc`) run without a shell; `cmd_shell` runs any script and always waits for the owner. The owner decides in the admin UI on the same computer: `/console` (live terminal, a tab per workspace plus System, approval cards) and `/commands` (Off / Ask / Auto per operation, trusted project files, shells, limits). Contracts live in `Waymark.Core/Commands`; the engine in `Waymark.Modules.Commands`; the pages in `Waymark.Host`. Rules, defaults and settings: `commands-policy.md`.
