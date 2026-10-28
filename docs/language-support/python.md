# Language support: Python

Branch: `feature/polyglot-node`. Status: research done, nothing implemented yet.
Builds on the shared groundwork in [typescript-node.md](typescript-node.md): language registry (`CodeLanguages`, `ILanguageIndexer`), migration 013, the `pkg_*` tools with an `ecosystem` parameter, and `cmd_project` with a family per toolchain. Python adds no tools; the count stays 32.

## What works after this plan

- .NET API + Python service (FastAPI, Flask, Django) in one session; Python + TS frontend; Python-only repos.
- `code_outline`, `code_method`, `code_search`, `code_types`, `code_grep` on `.py` and `.pyi`. `code_callers` works but is weaker than for C# (see Limits).
- `pkg_find` / `pkg_expand` with `ecosystem: "pypi"` at the version the lockfile pins.
- `cmd_project` on a `pyproject.toml`: install, test (pytest), lint (ruff), typecheck (mypy or pyright), run, package add/remove.

## Decisions still open (owner)

| # | Decision | Options | Recommended |
|---|---|---|---|
| P1 | Tools to support first | uv, pip + requirements.txt, Poetry, PDM, pip + pylock.toml | uv and pip first (uv.lock / requirements.txt with `==` pins), Poetry second, pylock.toml when pip's support leaves experimental |
| P2 | Order relative to TypeScript | after TS phase 1, or in the same phase | Same migration 013 either way (the CHECK lists `python` from the start); Python indexer right after TS phase 1, since it reuses all of its plumbing |
| P3 | TOML reader | add a TOML package, or a minimal reader for the few keys needed | a package (pyproject.toml, uv.lock, poetry.lock and pylock.toml are all TOML); confirm its API with the nuget tools when adding it |
| P4 | Jupyter notebooks | index `.ipynb` code cells, or grep only | grep only for now |

## Facts the plan rests on

- The tree-sitter Python grammar is in the TreeSitter.DotNet bundle (same package the TS plan adds), so indexing needs no new native dependency.
- Shape of a package follows PEP 561 order: a `<pkg>-stubs` package, then `.pyi` / inline types in a package with a `py.typed` marker, then typeshed `types-<pkg>` stubs, then plain `.py`. Unlike JS, plain `.py` still carries names and parameters, so even an untyped package gives a useful outline.
- Wheels are zip files with no install hooks; source distributions (sdists) run build code on install. `--only-binary :all:` refuses sdists and is recommended with `--require-hashes`. pip has no per-package allow-list for sdist builds.
- PEP 751 `pylock.toml` is the standard lockfile. uv keeps its own `uv.lock` as primary and exports pylock.toml; pip reads pylock.toml since 26.1, still marked experimental; Poetry has not shipped support (as of April 2026).
- Distribution name and import name can differ (Pillow -> `PIL`, scikit-learn -> `sklearn`, PyYAML -> `yaml`). The installed `*.dist-info/top_level.txt` or `RECORD` gives the mapping.
- Compiled extension modules (`.pyd`, `.so`) have no source; their shape exists only if the package ships `.pyi` stubs (numpy, for example, does).
- Running the project's own interpreter means running a file inside the workspace (`.venv\Scripts\python.exe`). `ExecutableLocator` deliberately searches only PATH to avoid exactly that, so the venv interpreter needs its own, explicit rule.

## Layer by layer

### 1. Core contracts

| File | Change |
|---|---|
| `Code/CodeIndexModels.cs` | `CodeLanguage` += `Python` |
| `Code/CodeLanguages.cs` | `.py`, `.pyi` -> Python; fence `python` |
| `Code/CodeFileClassification.cs` | Python uses: `test` (`test_*.py`, `*_test.py`, `conftest.py`), `declarations` (`.pyi`), `controller` (route decorators: `@app.get`, `@router.post`, `@app.route`, Django `urls.py`), `config` (`settings.py`, `conftest.py`), `source` |
| `Security/FileAccessPolicy.cs` | allowed += `.py`, `.pyi`, `.cfg`, `.ini` (setup.cfg, tox.ini, pytest.ini); blocked dirs += `.venv`, `venv`, `__pycache__`; blocked names += `.pypirc`, `pip.conf`, `pip.ini` (index credentials) |
| `Workspaces/WorkspacePathRules.cs` | excluded += `.venv`, `venv`, `env`, `__pycache__`, `.mypy_cache`, `.pytest_cache`, `.ruff_cache`, `.tox`, `*.egg-info` |
| `Workspaces/WorkspaceModels.cs`, `WorkspaceScanner.cs` | `PythonProjects` (`pyproject.toml`, `setup.py`, `requirements*.txt`), tool detected from `uv.lock` / `poetry.lock` / `pylock.toml` / requirements |
| `Commands/CommandCatalog.cs` | `PythonFamily = "python"`: `python.info` Auto, `python.lint` Auto (ruff runs no project code), `python.typecheck` Auto for pyright / Ask for mypy (mypy plugins are project code), `python.venv` Ask, `python.install` Ask, `python.test` Ask, `python.format` Ask, `python.run` Ask, `python.package_add` Ask, `python.package_remove` Ask |
| `Commands/RiskTiers.cs` | network: install, package_add; project code: test, run, typecheck (mypy) |
| package key (`Packages/*`) | ecosystem `pypi`; symbol kinds reuse the TS additions (`Function`, `Variable`, `TypeAlias`) |

### 2. SQLite

| File | Change |
|---|---|
| `Migrations/013_polyglot_code_index.sql` | `language` CHECK lists `'python'` from the start, so Python needs no second table rebuild |
| `Migrations/014_packages_ecosystem.sql` | ecosystem CHECK lists `'pypi'` |
| stores | nothing beyond the TS plan; language and ecosystem are data |

### 3. Python language project (new `src/Waymark.Languages.Python`)

Same shape as the TypeScript project, same TreeSitter.DotNet reference.

| File | Purpose |
|---|---|
| `PythonIndexer.cs` | `ILanguageIndexer` for `.py` / `.pyi`; same hash, size and limit rules |
| `Queries/*.scm` | classes, functions, decorators, assignments, imports, calls |
| `PySyntaxExtractor.cs` | class -> `Class`, or `Interface` when it derives from `Protocol`/`ABC`, `Enum` for `Enum` subclasses, `Record` for `@dataclass` / pydantic `BaseModel`; `def` in class -> `Method` (`__init__` -> `Constructor`, `@property` -> `Property`); module `def` -> `Function`; annotated class attributes -> `Field`; module assignments -> `Variable`; `TypeAlias` / `type X = ...` -> `TypeAlias`; decorators -> `Attributes`; first-statement docstring -> `DocSummary`; `import` / `from` -> `Usings`; bases without keyword args (`metaclass=`) -> `BaseList`; leading `_` -> private modifier; `__all__` marks the public surface; `self.x()` gets the enclosing class as receiver type |
| `PyFileClassifier.cs` | rules in section 1 |
| `StubReader.cs` | reads a package's `.pyi` / `.py` for the packages module, honouring `__all__` and `if TYPE_CHECKING` |

### 4. Code module

No Python-specific code: the TS plan already moves every `.cs` check to the language registry. Additions only:

| File | Change |
|---|---|
| `CodeModule.cs` | register `PythonIndexer` |
| `CodeIndexOptions.cs` | default `IndexExcludedFolders` += `.venv`, `venv`, `__pycache__`, `build`, `site-packages` |
| `Skeleton/FolderScan.cs` | project markers += `pyproject.toml`, `setup.py` |
| `Skeleton/PythonProjectSummarizer.cs` (new) | name, `requires-python`, tool (uv / Poetry / pip), dependency groups, pins from `uv.lock` / `poetry.lock` / `pylock.toml` / `==` lines in requirements; marks unpinned ranges |
| `Guide.md` | Python rows; snake_case vs camelCase when searching across a FastAPI backend and a TS frontend |

### 5. Packages module (ecosystem `pypi`)

| File | Purpose |
|---|---|
| `Pypi/LockReader.cs` | exact version and sha256 per distribution from uv.lock, poetry.lock, pylock.toml, or `==` + `--hash` lines |
| `Pypi/LocalSitePackages.cs` | prefers the project venv: `*.dist-info/METADATA` version equals the pin; `top_level.txt` / `RECORD` gives import names |
| `Pypi/PypiClient.cs` | public index only: `https://pypi.org/pypi/{name}/{version}/json`; timeout, retry with backoff, size limit; private indexes out of scope (their credentials live in pip.conf / .pypirc, which Waymark does not read) |
| `Pypi/WheelReader.cs` | picks a wheel (`py3-none-any` first, else one for this platform), never an sdist; verifies sha256; extracts only `.py`, `.pyi`, `py.typed`, `METADATA`, `top_level.txt`; refuses `..` and oversize entries |
| `Pypi/StubResolver.cs` | PEP 561 order: `-stubs` package, inline `.pyi`, `py.typed` sources, `types-<name>` from PyPI, plain `.py`; compiled-only module without stubs -> "no declared shape" |
| `Pypi/PypiSymbolFactory.cs` | `StubReader` output -> symbol entries with the same sections (sig, methods, props, data, doc, rel); namespace = dotted module path |
| options | cache folder `%LOCALAPPDATA%\Waymark\pypi`, index URL, limits |
| `Guide.md` | `pkg_*` with `ecosystem: "pypi"`; distribution name vs import name; untyped packages give names and parameters without types |

### 6. Commands module

| File | Change |
|---|---|
| `Recipes/PythonRecipes.cs` (new) | info (python, uv, pip versions); venv (`uv venv` or `py -m venv .venv`); install: uv `uv sync --frozen --no-build`, Poetry `poetry install --no-root` with `installer.only-binary`, pip `pip install -r <file> --require-hashes --only-binary :all:` (no `--require-hashes` when the file has no hashes, said in the result); test `pytest -q`; lint `ruff check --output-format concise`; format `ruff format`; typecheck `pyright` or `mypy`; run `python -m <module>`; package_add `uv add` / `poetry add` with an exact version |
| `Recipes/RecipeContext.cs` | `ExistingPythonProject(key)`, the project's venv interpreter |
| `Recipes/RecipeRules.cs` | PEP 508 name and exact version validation; module name validation for `run` |
| `Execution/ExecutableLocator.cs` | `uv`, `py` and `python` from PATH; the project interpreter only from `<project>/.venv/Scripts/python.exe` (or `bin/python`), an explicit exception that is shown on the approval card and makes the operation drift-sensitive |
| `Execution/ChildEnvironment.cs` | forced `PYTHONUNBUFFERED=1`, `PYTHONIOENCODING=utf-8`, `PIP_NO_INPUT=1`, `PIP_DISABLE_PIP_VERSION_CHECK=1`; never pass `PYTHONPATH`, `PYTHONSTARTUP`, `PYTHONHOME` (allow-list already keeps them out) |
| `Execution/BuildFingerprint.cs` | += `pyproject.toml`, `setup.py`, `setup.cfg`, `uv.lock`, `poetry.lock`, `pylock*.toml`, `requirements*.txt`, `conftest.py` (pytest runs it), `tox.ini`, `noxfile.py`, `.python-version`, `.venv/pyvenv.cfg`; skip `.venv`, `__pycache__` |
| `Execution/DiagnosticsParser.cs` | colon formats: ruff `file:line:col: CODE msg`, mypy `file:line: error: msg [code]`, pyright `file:line:col - error: msg`; pytest totals `N failed, M passed in Xs` and `FAILED path::test` lines |
| `Reporting/RunReport.cs` | parse diagnostics for `python.*` |
| `Recipes/ProcRecipes.cs` | `stop_port` may end the venv interpreter of this workspace (it runs from inside it, so the existing rule already allows it) |

### 7. Host and UI

| File | Change |
|---|---|
| `Components/Pages/Workspaces.razor(.cs)` | Python projects, tool, venv found or not |
| `Components/Pages/CommandSettings.razor(.cs)` | `Group(PythonFamily, "Python")`; build-file text names pyproject.toml, lockfiles, conftest.py |
| Packages and explorer pages | ecosystem `pypi`, link to `pypi.org/project/{name}/{version}` |
| `RiskGuide.razor` | sdist builds run code; pip.conf / .pypirc hold index credentials |

### 8. Docs and tests

- Same documents as the TS plan, plus a Python section in `docs/commands-policy.md` (only-binary, hashes, venv rule).
- Tests: extractor fixtures (decorators, dataclasses, Protocols, nested functions, async, overloads, `if TYPE_CHECKING`, syntax errors), lock readers (uv, poetry, requirements with hashes), wheel reader (path traversal, hash mismatch), stub resolver order, diagnostics on ruff / mypy / pyright / pytest output.

## Phases

Each phase ends with a clean `rebuild`, the owner's reinstall task and the owner's confirmation.

| Phase | Scope | Done when |
|---|---|---|
| Py-1 Index | sections 1 (code rows), 3, 4 (after TS phase 1) | outline, method, search and types correct on a FastAPI and a Django project; C# and TS output unchanged |
| Py-2 Workspaces and skeleton | workspace metadata, `PythonProjectSummarizer`, access policy | skeleton shows locked pins for a uv and a pip project |
| Py-3 Commands | section 6 | install refuses sdists, pytest / ruff / pyright results parsed |
| Py-4 Packages | section 5 | `pkg_expand` right for an inline-typed package, a stub-only package (`types-requests`) and an untyped one; Pillow resolves to `PIL` |

## Limits to state in the guide

- Python is dynamic: monkeypatching, `getattr`, `**kwargs` forwarding and decorators that change signatures are invisible to a syntax index. Receiver types are known mostly for `self`, so `code_callers` returns many `?` receivers. A test run is the proof, not the index.
- Notebooks (`.ipynb`) are searchable with `code_grep` only.
- Private package indexes, conda environments and system-wide installs are out of scope.
- Native extensions without stubs have no shape.

## Risks

- `--only-binary :all:` fails for packages that publish only an sdist; the result must say which package and that the owner can allow a build through `cmd_shell`.
- Keep pip current: CVE-2026-13346 (doubly encoded URLs from an index could place files outside the target) was fixed in a 2026 pip release; `python.info` should report the pip version.
- A TOML dependency is new to the solution (decision P3).
