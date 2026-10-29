# Waymark code tools

Find before you read, and read only what the task needs: a full read of a large file costs context that the targeted calls avoid. Workspace names are exact; `code_skeleton('*')` lists them. The code tools understand C# (`.cs`), Python (`.py`, `.pyi`) and TypeScript and JavaScript (`.ts .tsx .mts .cts .js .jsx .mjs .cjs`); other files go through `code_read` and `code_grep`.

## Which call

| Need | Call |
|---|---|
| Workspace names | `code_skeleton('*')` |
| Overview: folder tree with sizes, solutions, projects, packages with pinned versions, SDK, target framework, Python projects | `code_skeleton(ws)` |
| Open a folder marked ▸, or one project's own package pins | `code_skeleton(ws, path='src/Orders')` |
| An exact number of levels (a large depth shows the whole tree) | `code_skeleton(ws, path?, depth=3)` |
| Folder shows 'N files collapsed'; does file X exist | `code_folder_files(ws, folder, searchPattern, page)` |
| What changed since a point in time (resuming, reviewing edits) | `code_changes_since(ws, ISO 8601 or unix seconds)` |
| Type, member or attribute by name | `code_search(query, ws or '*', memberType)` |
| Text in any file (config keys, routes, log messages, SQL, Razor) | `code_grep(ws or '*', text, pathGlob?)` |
| Structure of C# or Python files, no bodies | `code_outline(ws, 'A.cs,b.py')` |
| Member bodies with real line numbers | `code_method(ws, file, 'M1,M2', className?)` |
| A file the outline cannot read, a source file up to 15 KB, lines containing a word | `code_read(ws, file, startLine + endLine or query)` |
| Who calls a method or function | `code_callers(ws, file, method)` |
| Who implements or derives from a type | `code_types(ws, typeName)` |
| Lines or a file to move or copy (also to another workspace) | `code_outline` / `code_method` give the line numbers; pass them as `from` to `file_edit_lines` / `file_write` (files guide). No need to read the body. |

Batch lists are comma separated with no spaces; a batch outline may mix languages.

## Reading

| File | Do |
|---|---|
| C# or Python over 15 KB (⚠️ in the skeleton) | `code_outline`, then `code_method` for the members needed |
| C# or Python up to 15 KB (✓) | `code_read` with a line range, or `code_outline` when only structure matters |
| Anything else (.csproj, pyproject.toml, .razor, .md, Dockerfile) | `code_read` with a range or `query` |

- `code_outline` shows the public surface. Set `includePrivateMembers: true` for DI constructors, internals or private refactors.
- `code_read` takes one mode: full file, range (1-based, inclusive) or `query` (case-insensitive, returns numbered matching lines). Prefer range or query.
- `code_read` refuses appsettings*.json, secrets, .env, .npmrc, .pypirc, pip.conf, keys, database files and build output; do not look for a way around. For config shape, find the Options class with `code_search` and its binding; for values, ask the user.
- Before adding a feature, read one existing sibling (service, endpoint, DI registration, Python module) so new code follows the naming, layering, error handling and logging already used.

## Line numbers

Numbers from `code_method`, `code_read` and `code_outline` are real file lines and go straight into `file_edit_lines`. They stay valid only until that file changes: after any edit, read the range again. To move or duplicate code you do not need to read its body: give those line numbers as `from` (files guide, "Copy, don't retype") and the server copies it.

## Search

- `code_search` is forgiving and ranked best first: exact, prefix, camel-case initials ('PSS'), word start ('Skeleton'), several words ('search global' finds SearchGloballyAsync), substring, then characters in order. Narrow with `memberType`; `Interface` also finds implementers, `Attribute` finds attribute and decorator use.
- Hundreds of hits mean a more specific name or `memberType`, not more pages.
- `ws='*'` searches every enabled workspace, so a .NET backend and a Python service in two workspaces are found together; the other code tools need one workspace.
- Names follow each language's habit: a C# `TotalAmount` is `totalAmount` in JSON and `total_amount` in Python. Search for each spelling when a name crosses a boundary.
- `code_grep(ws or '*', text, pathGlob?)` finds literal text in any readable file (config keys, routes, log messages, SQL, JSON, YAML, TOML, Razor, markdown), not only code. It reads the files from disk on every call, so it is never stale and its line numbers are real lines. Use `code_search` for names and `code_grep` when the text is not a name.
- `code_grep` is plain text on one line, not a regex; narrow with `pathGlob` (`**/*.json`, `src/Orders/**`). It skips blocked files, vendored folders (wwwroot/lib, dist, vendor), minified, binary and very large files, and says how many it left out. A 'results are partial' note means narrow the search.
- The package part of the skeleton gives the pinned versions the `pkg_*` tools need: NuGet packages with their target framework, Python dependencies with the version their lock file pins. When the folder holds many projects it is one summary of every pinned version; `code_skeleton(ws, path=<project folder>)` gives one project's pins.

## Python

- `.py` and `.pyi` are indexed like C#. A class is a type (a `Protocol` is an interface, an `Enum` subclass an enum, a dataclass or pydantic model a record); a `def` inside a class is a method, `@property` a property. Functions and variables outside any class are listed under *Functions* and *Variables* in the outline and found with `memberType` Function or Variable.
- Decorators are the attributes: `code_search(..., memberType='Attribute')` finds `app.get`, `pytest.fixture`, `dataclass`. The first line of a docstring is the summary. A name with a leading underscore is private; when a module has `__all__`, a top-level name it does not list is internal. Both show only with `includePrivateMembers: true`.
- `__init__` is the constructor, named after its class: `code_method(file, '__init__')` returns it and `code_callers(file, 'ClassName')` finds the calls that create the class. Attributes set in `__init__` (`self.x = ...`) appear as fields.
- The *module* is the dotted path of the file (`app.models.user`), without a leading `src/`.
- Python is dynamic. A call's receiver type is known only for `self` and `cls`, and guessed for a capitalised name, so `code_callers` shows many calls marked `?`. Monkeypatching, `getattr` and decorators that change a signature are invisible. A test run is the proof, not the index.
- Blocked for every tool: `.venv`, `venv`, `__pycache__`, `site-packages` and the tool caches (`.mypy_cache`, `.pytest_cache`, `.ruff_cache`, `.tox`).

## TypeScript and JavaScript

- `.ts .tsx .mts .cts .js .jsx .mjs .cjs` are indexed like C# and Python. A class, an interface, an enum, a type alias and a namespace (or `declare module "x"`) are types; a method, a constructor, a get accessor, a field (a field that holds an arrow function is a method) and a constructor parameter with an access modifier (`private repo: Repo`) are members. Functions, arrow-function constants and other top-level constants are listed under *Functions* and *Variables*; `code_search` finds them with `memberType` Function or Variable, and a type alias with TypeAlias.
- Decorators (`@Component(...)`, `@Injectable()`) are the attributes. The first line of a JSDoc comment is the summary. The *module* is the path without extension (`app/user.service`), without a leading `src/`; `components/Button/index.tsx` is `components/Button`.
- A top-level name is public when it is exported (in a file with no import or export, such as a script or a `.d.ts`, everything is public); class members are public unless marked `private`, `protected` or `#name`. Non-public names show with `includePrivateMembers: true`.
- `.d.ts` files are classified *declarations*; `.tsx` / `.jsx` files that export PascalCase functions are *component* files; `*.test.*`, `*.spec.*` and `__tests__` are tests; `vite.config.ts` and friends are configuration.
- `code_callers` sees `foo()`, `obj.foo()` and `new Foo()`. A receiver type is known only for `this` and guessed for a capitalised name, so expect `?` for everything else. JSX tags (`<Button />`) are not calls: search `Button` with `code_grep` for those.
- Minified and bundled files and build output (`dist`, `out`, `.output`, JavaScript in `build`) are left out of the index, and `.next`, `.nuxt`, `.svelte-kit`, `.turbo`, `.angular`, `coverage`, `node_modules` are blocked folders for every tool. Vue and Svelte single-file components are not parsed (`code_read` and `code_grep` work).
- A JSON field is `userName` in TypeScript, `UserName` in C# and `user_name` in Python: search for each spelling when a name crosses a boundary.
- `code_skeleton` lists Node projects (every `package.json`) with the version the lock file pins for each dependency, the package manager, TypeScript and Node versions, workspaces and scripts.
## Overview

- Without `depth`, `code_skeleton` opens folders level by level until a size budget is used, so it fits any workspace. A folder left closed is marked ▸ with a summary in brackets: folders, files, size, large files (⚠️), main extensions, project file. Counts cover only what the tools may open.
- Open a ▸ folder with `code_skeleton(ws, path=...)`; the notes under the tree name the largest closed folders. Vendored code (wwwroot/lib, dist, vendor) and folders with very many entries stay closed in the overview, and a folder with more entries than fit at once is paged with `page`.
- 'not scanned' and '≥' mean the scan hit its limit on a very large workspace: open a subfolder to see it in full.
- Migrating between two workspaces: `code_skeleton` each of them (`code_skeleton('*')` lists the names), compare the layouts, then copy folder by folder. The overview of the target also shows which packages and target framework the copied code will compile against.

## Impact before changing a contract

| Change | First |
|---|---|
| Method or function signature or behavior, rename, delete | `code_callers` (`includeTests: true` when tests matter) |
| Interface or base class | `code_types` (`memberName` shows implementations and overrides) |
| Move or delete a file | `code_search` for its type names and namespace or module |

`code_search`, `code_callers` and `code_types` read a syntax index and match by simple name, without symbol resolution, so they can miss a usage (reflection, generated code, dynamic Python). In callers, `?` marks an unknown receiver type and `includeUnrelated: true` lists same-named methods of other types. Zero results is not proof of zero usages; a clean build (`cmd_project`, commands guide) or a passing test run is. `code_outline`, `code_method` and `code_read` parse the file on disk, so they are never stale.

## Errors

| Message | Action |
|---|---|
| Unknown workspace | `code_skeleton('*')` for the exact name |
| File refused | see Reading; do not retry with another tool |
| 'not a source file the code tools can analyze' | `code_read` the file instead |
| 'parser did not load' | the tree-sitter parser (Python, TypeScript and JavaScript) could not start on this computer; `code_read` still works, and C# is not affected |
