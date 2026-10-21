# Waymark file tools

These tools change files in a workspace. The owner switches them on in Waymark (a tool that is not listed is off: ask the owner), and every change is written to the audit trail. Unless the user already told you to proceed, state the planned change and get a yes first. There are no backups: undo is git (`cmd_git`).

## Which call

| Situation | Call |
|---|---|
| Part of an existing file | `file_edit_lines` |
| New files, or a rewrite of more than 80% of a file | `file_write` (one call, many entries) |
| Move or rename files | `file_move` |
| Move or rename a folder | `folder_move` (one per call, on purpose) |
| Delete | `file_delete`, `folder_delete` |
| Empty folders | `folder_create` (rarely needed: `file_write` creates parent folders) |

## file_write

`filesJson` is a JSON array of `{"relativeFilePath":"Services/OrderService.cs","content":"...","mode":"create"}`.

- Always set `mode`. Left out, it is upsert, which silently overwrites. `create` fails if the file exists (a free existence check); `overwrite` fails if it is missing.
- An existing file keeps its encoding and line endings; new files are UTF-8.
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
