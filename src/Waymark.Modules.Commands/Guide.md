# Waymark command tools

Typed tools run programs in a workspace without a shell, and the owner watches every run live in the Waymark Console. A tool that is not listed is switched off: ask the owner. Option names and values are in each tool's own description; this guide covers how runs behave and how to work with them.

## Which tool

| Need | Tool |
|---|---|
| build, test, format, packages, EF Core, run a .NET project; install, test, lint, type-check, run a Python project (actions starting with `py_`); install, type-check, build, test, run a Node project (actions starting with `node_`) | `cmd_project` |
| git | `cmd_git` |
| ports, stop a program, build servers, delete bin/obj | `cmd_proc` |
| anything none of those can do | `cmd_shell` (last resort) |
| follow or inspect a run | `cmd_wait`, `cmd_output`, `cmd_list`, `cmd_stop` |

Prefer a typed tool: it is checked and can run without waiting for the owner. Give `purpose` as one sentence: the owner reads it before approving, sees it on the live log, and it is kept in the audit log.

## How a run behaves

- Each operation has a gate the owner chose: Off (refused), Ask (waits for approval) or Auto (runs at once). The result says which. You cannot approve, deny or change a gate.
- A tool call lasts at most 60 seconds, so every run is detached and returns a run id. `waitSeconds` is at most about 45; when the run is still going, call `cmd_wait(runId)` again until it ends.
- Waiting for approval means nothing has run. Tell the user to approve it in the Waymark admin UI (Console page), then `cmd_wait`. An undecided request is denied after about 10 minutes. After a denial do not resend the same command; ask the user why.
- `cmd_output(runId)` shows the plain-text output, last lines by default. `grep` finds lines; `fromLine` with `lines` (up to 500) pages through it. A build prints thousands of lines, so read the errors in the result first.
- `cmd_list(workspace?, limit up to 30)` shows recent runs with their ids and status.
- `cmd_stop(runId)` ends a run that waits, is queued or is running (a `run` action, say), with its whole process tree. It does nothing for a finished run.
- Operations that change files run one at a time per workspace; a second one is queued.

## cmd_project: .NET

When `target` is left out, the only .sln or .slnx in the workspace root is used.

| Gate by default | Actions |
|---|---|
| Auto | build, rebuild, clean, restore, format_check, info, list |
| Ask | test, format, new, sln, reference, package_add, package_remove, tool_restore, ef_list, ef_migrations_add, ef_migrations_remove, ef_database_update, run |

Builds and many git commands load files that can run code (props and targets, Directory.Build.*, global.json, nuget.config, solution files, git hooks). The first Auto run after any of those changed asks the owner once, and the approval card names the changed files.

### Build until clean

1. `build`. The result lists compiler errors and warnings with workspace-relative paths, so warnings are always included.
2. For each error: read the location (`code_read`, line range), fix it (`file_edit_lines`, one call per file), rebuild. Fix errors before warnings, because errors cause spurious warnings.
3. Repeat until zero errors and zero warnings, then run `rebuild` once. Only a clean full rebuild means done.
4. A warning that existed before your change: tell the user, and leave unrelated code alone. Fix the ones you introduced.
5. The same error after two attempts: stop, re-read the surrounding code and the callers; do not loop.
6. Several projects: build the solution (leave `target` out) so dependents compile too.
7. A clean build is not proof that it works. For behavior, run `test` with `filter`.
8. After code was copied or migrated (`from` / `fromFolder` in the files tools), build the target solution at once. Typical errors are CS0246 / CS0234 (a using, namespace or project reference the copied code needs) and duplicate definitions; fix them with small patches, not by copying again.

### Adding a package

Confirm with the user first, because it edits the .csproj. Use `package_add` with `project`, `package` and `version`: the latest stable that fits the project's target framework, preferring the version other projects here already pin (`pkg_search` shows the latest). Then check the pinned version in `code_skeleton(ws, path=<project folder>)` and build.

## cmd_project: Python

Python actions start with `py_` and work on one project: option `project` is its folder (or its pyproject.toml or requirements file), default the workspace folder, and the command runs there. `py_info` shows what this computer has (Python, pip, uv, ruff, pyright, mypy) and what the project has (lock file, virtual environment); call it first when you do not know.

| Gate by default | Actions |
|---|---|
| Auto | py_info, py_lint |
| Ask | py_list, py_venv, py_install, py_test, py_format, py_typecheck, py_run, py_package_add, py_package_remove |

### How the Python actions keep the owner safe

- **Installs run no package code.** Wheels only: uv gets `--no-build`, pip gets `--only-binary :all:`, and the project itself is never built (`--no-install-project`). A package that has no wheel fails with a message that names it; the owner can install it through `cmd_shell`. Waymark never installs outside the project's virtual environment (`PIP_REQUIRE_VIRTUALENV` is set).
- **A requirements file is read before it is used.** Editable installs, paths, addresses (`git+...`, `name @ url`) and options such as `-r`, `-c`, `-f`, `--no-binary` are refused, because they build or fetch code that was not checked. When the file has `--hash=` lines, `--require-hashes` is added.
- **The project's own interpreter is a program inside the workspace.** `py_test`, `py_run`, `py_list`, mypy and pip use `.venv/Scripts/python.exe` (`.venv/bin/python` elsewhere), because they must run the project's code. The command line and the approval card show it; those actions ask by default and count as running code. `py_lint` and `py_format` use the `ruff` found on PATH only, never one from the workspace.
- **Poetry and PDM projects are not run yet.** `py_install` and `py_package_*` say so; use `tool: "pip"` with a requirements file or `cmd_shell`.

### Typical flows

1. Fresh project with a `uv.lock`: `py_install` (uv creates `.venv` itself), then `py_test`. With only a requirements file: `py_venv`, `py_install`, `py_test`.
2. `py_test` results list the failed tests (`failed: tests/test_a.py::test_b`) and the last lines. Read the failing test and the code it calls (`code_method`), fix, run `py_test` again with `filter` or `paths` set to just that test. Run the whole suite once at the end.
3. `py_lint` and `py_typecheck` list findings as `path(line): CODE message`. Fix errors before warnings; the same finding after two attempts means re-read the code.
4. Adding a package: look it up first (`pkg_find` with `ecosystem: "pypi"`), confirm with the user, then `py_package_add` with the exact `version`. It edits pyproject.toml and uv.lock. A pip project changes its requirements file with `file_edit_lines` instead, then `py_install`.
5. `py_run` keeps a server running until stopped: give the user the run id and let them watch the Console. End it with `cmd_stop(runId)` (that ends its whole process tree); `cmd_proc stop_port` queues behind a running `py_run` in the same workspace, so use it only for a server that was started elsewhere.
6. A passing test run is the proof that a refactor works. The code index cannot see dynamic Python, so a clean `py_lint` is not proof.

## cmd_project: Node

Node actions start with `node_` and work on one project: option `project` is its folder (or its package.json), default the workspace folder, and the command runs there. `node_info` shows what this computer has (Node, npm) and what the project has (package manager, node_modules, TypeScript, scripts); call it first when you do not know.

| Gate by default | Actions |
|---|---|
| Auto | node_info |
| Ask | node_list, node_install, node_typecheck, node_build, node_test, node_lint, node_script, node_run, node_package_add, node_package_remove |

### How the Node actions keep the owner safe

- **npm is started as `node npm-cli.js`**, never as the `npm.cmd` shim: a .cmd file is read by cmd.exe, which would interpret the arguments.
- **Installs run no lifecycle scripts.** `node_install` is `npm ci --ignore-scripts` from `package-lock.json` (it refuses a project without one); `node_package_add` is `npm install name@exact --save-exact --ignore-scripts`. A package that needs its install script (esbuild, sharp, prisma) must be set up by the owner through `cmd_shell`.
- **A script runs only when package.json names it.** `node_build`, `node_test`, `node_lint`, `node_script` and `node_run` read package.json first; an unknown name is refused with the list of real ones, and the approval card shows the script's text. Extra `args` go after `--`, limited to plain characters; options that load or evaluate code (`--require`, `--import`, `--loader`, `--eval`, `--inspect`) are refused.
- **npm only for installs.** A pnpm, yarn or bun project can still build, test and run its scripts (`npm run`), but `node_install` and `node_package_*` refuse it and say so; use `cmd_shell` for those.
- `CI=1` is set for every run, so test runners do not start in watch mode.

### Typical flows

1. Fresh clone with a lock file: `node_install`, then `node_typecheck`, then `node_test`. The typecheck result lists `file(line): error TS...` findings; fix errors before warnings and run it again.
2. A failing test run lists the failed tests (`failed: ...`) and the last lines; read the test and the code it calls (`code_method`), fix, and run `node_test` again with `args` naming just that test (for vitest and jest `["-t", "name"]`, or a file path). Run the whole suite once at the end.
3. Adding a package: look it up first (`pkg_find` with `ecosystem: "npm"`), confirm with the user, then `node_package_add` with the exact `version` (and `dev: true` for a development dependency). It edits package.json and package-lock.json.
4. `node_run` keeps a dev server running until stopped: give the user the run id and let them watch the Console. End it with `cmd_stop(runId)`.
5. Starting from an empty folder: write package.json, tsconfig.json and the sources with the file tools, create the lock file with `npm install --package-lock-only --ignore-scripts` through `cmd_shell`, then `node_install`.

## cmd_git

- Reading (status, diff, log, show, branches, stashes, remotes) is automatic. Changes ask the owner unless the owner set them to Auto. Nothing can force-push.
- `reset_hard`, `clean` and `branch_delete` are off unless the owner enables them. Use `clean` with `dryRun` first.
- Commit, push and branch changes only when the user asked for them.
- git is the undo for the file tools: `status` and `diff` show what changed.

## cmd_proc

Use it for a port that stays busy (`ports`, then `stop_port`), for files locked by compiler servers (`build_server`), or for stale build output (`clean_outputs` with `dryRun` first). `stop_port` only ends programs that run from the workspace, or that are dotnet, Python or Node programs, never Waymark.

## cmd_shell

Every script waits for the owner, who may edit it before approving; the edited text is what runs. So:

- Write for the shell that will run it (PowerShell on Windows unless you name another with `shell`).
- Keep it short and do one thing: it is approved faster.
- Nothing can be typed to it while it runs.
- `timeoutSeconds` is 5 to 3600 (default 300). With `workspace` it starts in that folder; without, it is a system script that starts in the home folder.
- Plan for slow approval: `waitSeconds` returns a run id, then use `cmd_wait`.

### Long runs and hand-offs

A server, a watcher, a big test run or an installer can take minutes. Start it with a short `waitSeconds`, give the user the run id, say what is running and that they can watch it live on the Console page, then stop and let them tell you when it is done. Do not sit in a `cmd_wait` loop: it spends your context to show what the user already sees. When they say it is done, read `cmd_output` and check the result (a status command, a health check) before you report success.

### Project scripts

Projects often ship their own script for a multi-step job (install.cmd, build.ps1, deploy.sh). Look for one (`code_skeleton`, the README, the docs) before redoing the steps by hand. Read it first (`code_read`) so you can tell the user what it does, run it with `cmd_shell` in the workspace, then hand off as above.

### Restarting Waymark itself

A run ends when Waymark stops. A script that restarts or reinstalls Waymark (an update, an installer) must start outside it, for example as a one-shot Windows scheduled task, or the restart kills it halfway. Tell the user the connection will drop and wait for them.

## Output

Results are plain text. Waymark hides secrets as `[redacted:...]` markers; never write a marker into a file or command. Full output is also kept for the owner in the Console.
