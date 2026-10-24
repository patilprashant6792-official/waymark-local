# Waymark code tools

Find before you read, and read only what the task needs: a full read of a large file costs context that the targeted calls avoid. Workspace names are exact; `code_skeleton('*')` lists them.

## Which call

| Need | Call |
|---|---|
| Workspace names | `code_skeleton('*')` |
| Overview: folder tree with sizes, solution, packages with pinned versions, SDK, target framework | `code_skeleton(ws)` |
| Open a folder marked ▸, or one project's own package pins | `code_skeleton(ws, path='src/Orders')` |
| An exact number of levels (a large depth shows the whole tree) | `code_skeleton(ws, path?, depth=3)` |
| Folder shows 'N files collapsed'; does file X exist | `code_folder_files(ws, folder, searchPattern, page)` |
| What changed since a point in time (resuming, reviewing edits) | `code_changes_since(ws, ISO 8601 or unix seconds)` |
| Type, member or attribute by name | `code_search(query, ws or '*', memberType)` |
| Text in any file (config keys, routes, log messages, SQL, Razor) | `code_grep(ws or '*', text, pathGlob?)` |
| Structure of C# files, no bodies | `code_outline(ws, 'A.cs,B.cs')` |
| Member bodies with real line numbers | `code_method(ws, file, 'M1,M2', className?)` |
| Non-C# file, C# up to 15 KB, lines containing a word | `code_read(ws, file, startLine + endLine or query)` |
| Who calls a method | `code_callers(ws, file, method)` |
| Who implements or derives from a type | `code_types(ws, typeName)` |
| Lines or a file to move or copy (also to another workspace) | `code_outline` / `code_method` give the line numbers; pass them as `from` to `file_edit_lines` / `file_write` (files guide). No need to read the body. |

Batch lists are comma separated with no spaces.

## Reading

| File | Do |
|---|---|
| C# over 15 KB (⚠️ in the skeleton) | `code_outline`, then `code_method` for the members needed |
| C# up to 15 KB (✓) | `code_read` with a line range, or `code_outline` when only structure matters |
| Non-C# (.csproj, .razor, .md, Dockerfile) | `code_read` with a range or `query` |

- `code_outline` shows the public surface. Set `includePrivateMembers: true` for DI constructors, internals or private refactors.
- `code_read` takes one mode: full file, range (1-based, inclusive) or `query` (case-insensitive, returns numbered matching lines). Prefer range or query.
- `code_read` refuses appsettings*.json, secrets, .env, keys, database files and build output; do not look for a way around. For config shape, find the Options class with `code_search` and its binding; for values, ask the user.
- Before adding a feature, read one existing sibling (service, endpoint, DI registration) so new code follows the naming, layering, error handling and logging already used.

## Line numbers

Numbers from `code_method`, `code_read` and `code_outline` are real file lines and go straight into `file_edit_lines`. They stay valid only until that file changes: after any edit, read the range again. To move or duplicate code you do not need to read its body: give those line numbers as `from` (files guide, "Copy, don't retype") and the server copies it.

## Search

- `code_search` is forgiving and ranked best first: exact, prefix, camel-case initials ('PSS'), word start, several words, substring. Narrow with `memberType`; `Interface` also finds implementers, `Attribute` finds attribute use.
- Hundreds of hits mean a more specific name or `memberType`, not more pages.
- `ws='*'` searches every enabled workspace; the other code tools need one workspace.
- `code_grep(ws or '*', text, pathGlob?)` finds literal text in any readable file (config keys, routes, log messages, SQL, JSON, YAML, Razor, markdown), not only C#. It reads the files from disk on every call, so it is never stale and its line numbers are real lines. Use `code_search` for C# names and `code_grep` when the text is not a name.
- `code_grep` is plain text on one line, not a regex; narrow with `pathGlob` (`**/*.json`, `src/Orders/**`). It skips blocked files, vendored folders (wwwroot/lib, dist, vendor), minified, binary and very large files, and says how many it left out. A 'results are partial' note means narrow the search.
- The NuGet part of the skeleton gives the pinned package versions and target framework that the nuget tools need. When the folder holds many projects it is one summary of every pinned version; `code_skeleton(ws, path=<project folder>)` gives one project's pins.

## Overview

- Without `depth`, `code_skeleton` opens folders level by level until a size budget is used, so it fits any workspace. A folder left closed is marked ▸ with a summary in brackets: folders, files, size, large files (⚠️), main extensions, project file. Counts cover only what the tools may open.
- Open a ▸ folder with `code_skeleton(ws, path=...)`; the notes under the tree name the largest closed folders. Vendored code (wwwroot/lib, dist, vendor) and folders with very many entries stay closed in the overview, and a folder with more entries than fit at once is paged with `page`.
- 'not scanned' and '≥' mean the scan hit its limit on a very large workspace: open a subfolder to see it in full.
- Migrating between two workspaces: `code_skeleton` each of them (`code_skeleton('*')` lists the names), compare the layouts, then copy folder by folder. The overview of the target also shows which packages and target framework the copied code will compile against.

## Impact before changing a contract

| Change | First |
|---|---|
| Method signature or behavior, rename, delete | `code_callers` (`includeTests: true` when tests matter) |
| Interface or base class | `code_types` (`memberName` shows implementations and overrides) |
| Move or delete a file | `code_search` for its type names and namespace |

`code_search`, `code_callers` and `code_types` read a syntax index and match by simple name, without symbol resolution, so they can miss a usage (reflection, generated code). In callers, `?` marks an unknown receiver type and `includeUnrelated: true` lists same-named methods of other types. Zero results is not proof of zero usages; a clean build is (`cmd_dotnet`, commands guide). `code_outline`, `code_method` and `code_read` parse the file on disk, so they are never stale.

## Errors

| Message | Action |
|---|---|
| Unknown workspace | `code_skeleton('*')` for the exact name |
| File refused | see Reading; do not retry with another tool |
