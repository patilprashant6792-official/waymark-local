# Modules

Waymark is a modular monolith. A module references `Waymark.Core` only; `Waymark.Data` is the only project that touches SQLite; `Waymark.Host` wires everything and serves the admin UI and the MCP endpoint.

| Module | Tools | Gate |
|---|---|---|
| Code | code_skeleton, code_folder_files, code_changes_since, code_outline, code_method, code_read, code_search, code_grep, code_callers, code_types | Read |
| NuGet | nuget_packages, nuget_outline, nuget_find, nuget_expand | Read |
| Files | file_write, file_edit_lines, folder_create, file_move, file_delete, folder_move, folder_delete | Confirm (off until switched on) |
| Commands | cmd_dotnet, cmd_git, cmd_proc, cmd_shell, cmd_wait, cmd_output, cmd_list, cmd_stop | Confirm for the four that start something (off until switched on); the read tools are Read. Each operation also has an Off / Ask / Auto gate chosen by the owner, see `commands-policy.md` |
| Host | waymark_ping, util_datetime | Read |

Not migrated on purpose: `http_request`, `list_environments`. `execute_dotnet_command` is replaced by the typed `cmd_dotnet`.

`code_grep` finds literal text in any readable file (not only C#) by reading the files from disk on every call, so it is never stale. It applies each workspace's access rules, skips vendored folders (`wwwroot/lib`, `dist`, `vendor`), minified, binary and over-1 MB files, and reports how many it left out. Limits are set under `Waymark:Code:Grep:*` (see `ContentOptions`). There is no content index in the database; a SQLite full-text index is only worth adding if scans prove slow on real workspaces.

## Adding a tool

1. Put the logic behind an interface in the module; register it in the module's `AddServices`.
2. Add a method with `[McpServerTool(Name = "...")]` to a tool class; add `[ToolGate(ToolGate.Confirm)]` if it changes anything.
3. Throw `McpException` for expected failures; use `OutputBudget` to bound output.
4. The registry, Tools page, pause switch, timeout and audit pick it up from the attribute. No other wiring.
5. A module with tools also ships a `Guide.md` (the usage text agents ask for), embedded as `Waymark.Guides.<module>.md` in its `.csproj`. Start-up fails without it. See `guides.md`.

## Data

Migrations are numbered files in `Waymark.Data/Migrations` (next: 012; 011 is `011_tool_guides.sql`). Writes go through `IWriteQueue`, reads through `ISqliteConnectionFactory`.

## Vault

Named secrets, encrypted with ASP.NET Data Protection (keys next to the database, DPAPI on Windows). The UI never shows a value. `VaultRules.Scrub` replaces secret values in text with `[secret:NAME]`. It is **not yet applied** to command output (open item, see `commands-policy.md`).

The Vault page is hidden from the admin menu and the command palette for now. The page, store and rules are kept for further implementation.

## Commands

Typed recipes (`cmd_dotnet`, `cmd_git`, `cmd_proc`) run without a shell; `cmd_shell` runs any script and always waits for the owner. The owner decides in the admin UI on the same computer: `/console` (live terminal, a tab per workspace plus System, approval cards) and `/commands` (Off / Ask / Auto per operation, trusted project files, shells, limits). Contracts live in `Waymark.Core/Commands`; the engine in `Waymark.Modules.Commands`; the pages in `Waymark.Host`. Rules, defaults and settings: `commands-policy.md`.
