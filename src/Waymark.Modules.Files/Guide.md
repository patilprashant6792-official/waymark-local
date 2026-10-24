# Waymark file tools

These tools change files in a workspace. The owner switches them on in Waymark (a tool that is not listed is off: ask the owner), and every change is written to the audit trail. Unless the user already told you to proceed, state the planned change and get a yes first. There are no backups: undo is git (`cmd_git`).

## Which call

| Situation | Call |
|---|---|
| Part of an existing file | `file_edit_lines` |
| New files, or a rewrite of more than 80% of a file | `file_write` (one call, many entries) |
| Move or duplicate lines or whole files (also between workspaces) | `from` in `file_edit_lines` / `file_write` (section "Copy, don't retype") |
| A whole folder tree into a new layout | `fromFolder` in `file_write` |
| Move or rename files | `file_move` |
| Move or rename a folder | `folder_move` (one per call, on purpose) |
| Delete | `file_delete`, `folder_delete` |
| Empty folders | `folder_create` (rarely needed: `file_write` creates parent folders) |

## file_write

`filesJson` is a JSON array of `{"relativeFilePath":"Services/OrderService.cs","content":"...","mode":"create"}`.

- Always set `mode`. Left out, it is upsert, which silently overwrites. `create` fails if the file exists (a free existence check); `overwrite` fails if it is missing.
- An existing file keeps its encoding and line endings; new files are UTF-8 (a new file made with `from` keeps its source's).
- The result gives line count, size and modified time per file.

## file_edit_lines

`patchesJson` is a JSON array of `{"action":"patch","startLine":45,"endLine":89,"content":"..."}`.

| action | Effect |
|---|---|
| patch | replace startLine..endLine (inclusive, 1-based) with content |
| insert | insert content after startLine; 0 prepends |
| delete | remove startLine..endLine |
| append | add content at the end; line numbers ignored |

Each rule prevents a known failure:

1. Send all edits to one file in ONE call. They apply bottom-up on the original line numbers; a second call would use stale numbers and land in the wrong place.
2. Take the numbers from your latest read of that file (`code_method`, `code_read`).
3. Ranges must not overlap; the call is rejected otherwise.
4. Send complete syntactic units with the file's indentation (a whole method or using block). Half a brace is the commonest build break.
5. Read the changed range again afterwards to confirm the placement.
6. Several files: one call per file, and finish all of them before building.

## Copy, don't retype

When code only moves or is duplicated, the server copies it. You write no code, so it costs no output and cannot be mistyped. Use it for any block of more than about 10 lines that stays mostly the same: moving a method to another class, extracting a class, duplicating a file, migrating legacy code into a new layout.

`file_edit_lines`: a patch, insert or append carries `from` instead of `content`. `file_write`: an entry carries `from` instead of `content`.

```json
{"action":"insert","startLine":12,"from":{"workspace":"Legacy","file":"Billing/InvoiceService.cs","startLine":40,"endLine":180,"expectFirstLine":"public async Task<Invoice> CreateAsync(","expectLastLine":"}","csharp":{"namespaces":{"Legacy.Billing":"App.Orders"},"identifiers":{"Invoice":"Order"}},"indent":-4}}
```

| `from` field | Meaning |
|---|---|
| `file` | Source file. Default in `file_edit_lines`: the file being edited (duplicate or move inside it). Required in `file_write`. |
| `workspace` | Source workspace, any enabled one. Default: the workspace of the call. |
| `startLine`, `endLine` | Lines to copy (from `code_outline` / `code_method`; you need not read them). Leave both out for the whole file. |
| `expectFirstLine`, `expectLastLine` | Text of those lines, compared trimmed. If they differ, the file changed: nothing is copied. Always give both for ranges over 10 lines. |
| `remove` | `true` cuts: the lines also leave their file. Both `expect...` fields are required. Changes both files or neither. `file_edit_lines` only; for a whole file use `file_move`. |
| `replace` | `[{"find":"a","with":"b","count":3}]`, literal and in order. The number of matches must equal `count`, otherwise nothing is copied. Use it for non-C# text. |
| `csharp` | `namespaces` renames a namespace and everything below it, in `namespace`, `using`, `using static`, aliases and qualified names. `identifiers` renames every identifier token with that name. Strings and comments are never touched. It works on syntax, not meaning: a same-named member of another type changes too, so check the counts in the result. |
| `indent` | Shifts every line: `-4` removes up to 4 leading spaces or tabs (block namespace to file-scoped), `4` adds 4 spaces. |

Order of transforms: `replace`, `csharp`, `indent`. Line numbers in `from` are those of the source as it is now; in a `file_edit_lines` call they are not shifted by the other patches of the call.

Rules that prevent known failures:

1. A copy that holds a secret (vault value or a recognisable key, token, password) is refused; copy a range without it. You never see the secret, so do not retype it.
2. A new file made with `from` keeps the encoding and line endings of its source. Use `mode: create` so nothing is overwritten.
3. Binary files, files over the copy limit and files the access rules block are refused. Cutting from a file in another workspace needs that workspace to be enabled.
4. `dryRun: true` runs every check and transform and shows the line, match and rename counts without writing. Use it first when a rename or replace is involved.
5. Copy first, then edit what differs with small patches. Build afterwards: a clean build is the proof.

### Many files: `fromFolder`

An entry of `file_write` with only `fromFolder` copies a folder tree: `{"fromFolder":{"workspace":"Legacy","folder":"src/Billing","toFolder":"src/Orders","glob":"**/*.cs","pathRename":[{"find":"Invoice","with":"Order"}],"csharp":{...},"confirmCount":37}}`.

1. Run it with `dryRun: true`: it lists every file with its target and gives the count. Show the user the plan for anything over 5 files.
2. Run it again without `dryRun` and pass the count as `confirmCount`. If the tree changed in between, the call is refused.
3. At most 500 files per call. A file that fails (binary, secret, target exists) fails alone and the others are copied; keep the result as the migration record, since git cannot follow a copy between repositories.
4. Blocked folders, links and files the access rules refuse are left out.
## Structure changes

| Tool | Input | Behavior |
|---|---|---|
| `file_move` | `movesJson`: `[{"from":"a/Old.cs","to":"b/New.cs"}]` | Same folder renames, another folder moves. All entries are checked first; one bad entry aborts the batch. Never overwrites. |
| `folder_move` | `from`, `to` | Moves the contents; the code index follows. |
| `file_delete` | `pathsJson` | Per-file results; partial success is possible, so report each failure. |
| `folder_delete` | `pathsJson` | Recursive, deepest first; missing folders are skipped; a folder that contains a link is refused. |
| `folder_create` | `pathsJson` | Idempotent. |

Blocked by design (do not look for a way around): bin, obj, .git, .vs, .ssh, node_modules, backups, logs, packages, and files that hold secrets, keys or databases.

Before a move or delete, check the impact (`code_callers`, `code_types`, `code_search`, and `code_grep` for text in non-C# files; code guide).

## After a move, rename or delete

Moves leave C# namespaces and usings alone, and renaming a file does not rename its type.

1. `code_search` the old namespace or type name, and `code_grep` it for references outside C# (.csproj, .razor, json, docs).
2. Fix each file with `file_edit_lines` (one call per file), including the type and its references for a rename.
3. Build with `cmd_dotnet`: CS0246 and CS0234 errors show what is left.

After a delete, build at once: a clean build is the only proof that nothing depended on it.

## Redaction markers

Waymark hides secrets in results with `[redacted:...]` markers. Never write a marker into a file or command; the call is refused. Refer to a vault secret by name, or ask the user for the real value.
