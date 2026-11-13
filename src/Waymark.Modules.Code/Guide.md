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
| Type, member or attribute by name, or code found by what it does (a few words) | `code_search(query, ws or '*', memberType?, terms?, pathGlob?)` |
| Text in any file (config keys, routes, log messages, SQL, Razor) | `code_grep(ws or '*', text, pathGlob?)` |
| Structure of C# or Python files, no bodies | `code_outline(ws, 'A.cs,b.py')` |
| Member bodies with real line numbers | `code_method(ws, file, 'M1,M2', className?)` |
| A file the outline cannot read, a source file up to 15 KB, lines containing a word | `code_read(ws, file, startLine + endLine or query)` |
| Who calls a method or function | `code_callers(ws, file, method)` |
| Who implements or derives from a type | `code_types(ws, typeName)` |
| Lines or a file to move or copy (also to another workspace) | `code_outline` / `code_method` give the line numbers; pass them as `from` to `file_edit_lines` / `file_write` (files guide). No need to read the body. |
| Start of work on a feature | `recall(ws, 'get', ids)` with an id from the live setup, or `recall(ws, 'list', query?)` to search (flows below) |
| The path of a feature changed | `memorise(ws, 'save_feature_map', name, steps, expectedVersion)`; a new trigger: `name`, `summary`, `steps` |

Batch lists are comma separated with no spaces; a batch outline may mix languages.

## Feature maps (flows)

A feature map is a **flow**: one trigger to one outcome as the ordered methods the request passes through, saved under a name after its trigger (`memorise-save`). It stores names, never code, so it survives edits and branch switches. `recall` describes the path without any code; you read only the steps your task needs with `code_method`.

- **Find.** The live setup of the overview lists the newest 15 flows per workspace (id, name, steps, one-line summary). Otherwise `recall(ws, action='list', query='words')`; a method name as the query finds the flows it is a step of. Do this before you explore a feature.
- **Read.** `recall(ws, action='get', ids='12')` returns one line per step: where it is, its signature, and a mark for the link from the step above. Use it instead of exploring again, and explore only what it lacks.
- **Marks.** `[calls it]` the step above calls this one. `[by name only]` a call of that name is there but its receiver type is unknown. `[declared: event]` a link the index cannot see (you wrote ~event, ~http, ~ui, ~di or ~job), a constructor, a file or a range. `[DOES NOT CALL]` both steps are in the code and the step above makes no such call: the flow is wrong, fix it. `NOT ON THIS BRANCH (kept)` the step is not in the code here (another branch, a rename, a deleted file); it stays stored for the branch that has it, so do not remove it for that reason. `moved? ->` a method of that name is in another file.
- **Change the code, then decide.** Most edits are inside a step and need no save. Save only when the path changed (steps added, removed, renamed or moved) or for a new trigger. A `🧭` line in the result of `file_edit_lines`, `file_write`, `file_move` or `file_delete` means the change touched a step of a flow and something no longer matches: update the flow in this task. Folder moves and deletes do not give this line.
- **Update, create, delete.** Same trigger and outcome: update that flow. A new trigger or a different outcome: create a flow. Two flows with the same trigger and outcome: keep the better one and delete the other with `memorise(ws, action='delete_feature_map', name)`.
- Say in one line in your final answer which flow you saved or updated (name and id), or why you saved none.

### Writing the steps

`memorise(ws, action='save_feature_map', name, summary, steps)` to create; `memorise(ws, action='save_feature_map', name, steps, expectedVersion)` to change a flow you read (the whole new steps, and the `v` that `recall get` showed; `newName` renames it). One step per line:

```
src/Orders/OrderEndpoint.cs:OrderEndpoint.Post                  | the trigger
  src/Orders/OrderService.cs:OrderService.Place                 | totals are computed here
    src/Orders/OrderRepository.cs:OrderRepository.Insert
  src/Orders/Mailer.cs:Mailer.Send ~event                       | sent after the order is stored
```

- A step is `path:Type.Member` (or `path:function` in Python and JavaScript). Where there is no method to point to (a Razor page, SQL, config) a step may be a file `path` or a range `path#L10-40`; those links are always declared.
- Two spaces of indent mean "called by the step above". The first line is the only trigger; a step is one level deeper than its parent at most; at most 8 levels and 30 steps. Over 15 steps, split the flow by outcome.
- Add `~event`, `~http`, `~ui`, `~di` or `~job` after a step when the code reaches it in a way a call cannot show. Add `| note` (at most 120 characters) for why a step matters.
- Each **new or changed** step must exist now, and each new link must be a call the code makes; steps that were saved before stay as they are, whatever branch they are on. A save that breaks this is refused with the list of what to fix.
- Creating a flow whose trigger another flow starts at is refused: update that flow, or delete one if they say the same thing, or save again with `distinct='how this one differs'` when the outcome differs.
- A save with a `v` that is no longer current is refused, and the reply contains the flow as it is now: merge your change into it and save again.
- A map saved before flows existed shows as unordered. When you use it, put its steps in call order under one trigger (or split it by trigger) and save it with `expectedVersion`.

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

- `code_search` has two uses, both in the one call.
  - **By name** (one identifier): exact, prefix, camel-case initials ('PSS'), word start ('Skeleton'), several words in one name ('search global' finds SearchGloballyAsync), substring, then characters in order (three characters or more, and only when nothing better matched). An exact name is always first. Narrow with `memberType`; `Interface` also finds implementers, `Attribute` finds attribute and decorator use.
  - **By description** (several words): the search also reads the split words of names, doc comments, paths and the words used inside member bodies (identifiers, comments, string words), with stemming ('creating' finds Create). Hits are ranked by how many of your words they cover, then by kind (types first, constructors folded into their type), callers (often-called methods first) and tests last; each hit lists the words it `matched:`.
  - **Words, not sentences.** Give 2 to 5 content words in the vocabulary the code probably uses ('refresh token rotate'), not a question. Filler words ('how', 'the') are dropped.
  - **Syntax.** `"two words"` must be adjacent; `+word` must match; `-word` must not match. `terms=['jwt','bearer']` adds synonyms or other spellings (each counts half a word); use it when the first try finds nothing, instead of rewording the query again.
  - **Narrow.** `pathGlob` ('src/Billing/**'), `matchIn` (`Names` = symbol names only, `Docs`, `Code` = body words), `includeTests=false` (hidden hits are counted).
  - A `+required` word must appear in a name, signature, doc comment or path, or among the body words; body words are single words (an unordered bag), never lines of code, so open a hit with `code_method` to read it.
  - **Phrases and path words.** A `"quoted phrase"` must be adjacent in a name, signature or doc comment; body words have no order, so with `matchIn=Code` a phrase means 'all these words'. A word found only in the owner or the file path counts about a third, and a one-word query ignores content hits that match only that way.
  - **Limits.** A query is at most 200 characters and 16 words with `terms`; a query of only filler words, and a `pathGlob` with `[ ] { }`, are rejected. `Matches: N+` means the full-text index found more candidates than it ranks, so narrow the query.
- Hundreds of hits mean a more specific name, `memberType`, `pathGlob` or a `+required` word, not more pages.
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
- A constant that wraps a function is a Function with the wrapper in its signature: `const TradeTable = forwardRef<Ref, Props>(({ ... }, ref) =>`, `memo(...)`, `observer(...)`, `createAsyncThunk("x", async () => ...)`.
- **Local members.** Inside a module-level function (a React component, a custom hook, a helper) the nested `function` declarations and the `const` that hold a function or wrap one (`useCallback(fn, deps)`, `debounce(fn)`) are *local* Functions; `useMemo(fn, deps)` values are local Variables, listed among the locals and marked `(value)`. State, refs, effects and plain values are not members. Locals have the modifier `local`, no type, and the lines of their own statement, nested up to four levels.
  - `code_outline` lists them indented under the function that declares them, so a 2,000-line component shows its handlers with line ranges.
  - `code_method(file, 'fetchTrades,handleSave')` returns them directly; the result says which function declares them. When the same name is declared in two components of one file, pass the component as `className`.
  - `code_method` on a member too large for one answer returns a map instead of a cut-off body: its locals with line ranges and its opening lines (props, state, refs).
  - `code_callers` works on a local, and the calls inside a local are reported under the local's name, not its component's. Two locals of the same name in one file share one result.
- Locals: `code_search` sees only the first 64 body words (`code_grep` for the rest) and labels hits `local member:`.
- `code_callers` on a local lists calls inside its declaring function only (`className`).
- `code_outline` marks exported module-level names: `export ` / `export default ` in front of a function or class, and a trailing `(export default)` on a default-exported constant (`const A = ...; export default A;`).
- Decorators (`@Component(...)`, `@Injectable()`) are the attributes. The first line of a JSDoc comment is the summary. The *module* is the path without extension (`app/user.service`), without a leading `src/`; `components/Button/index.tsx` is `components/Button`.
- A top-level name is public when it is exported, including `export { a }`, `export default a` and `export default memo(a)` (in a file with no import or export, such as a script or a `.d.ts`, everything is public); class members are public unless marked `private`, `protected` or `#name`. Non-public names show with `includePrivateMembers: true`; the locals of a function that is shown are always shown.
- `.d.ts` files are classified *declarations*; `.tsx` / `.jsx` files that export PascalCase functions are *component* files; `*.test.*`, `*.spec.*` and `__tests__` are tests; `vite.config.ts` and friends are configuration.
- `code_callers` sees `foo()`, `obj.foo()` and `new Foo()`. A receiver type is known only for `this` and guessed for a capitalised name, so expect `?` for everything else. JSX tags (`<Button />`) and handlers passed as props (`onClick={handleSave}`) are not calls: search them with `code_grep`.
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
