# Waymark command tools

Typed tools run programs in a workspace without a shell, and the owner watches every run live in the Waymark Console. A tool that is not listed is switched off: ask the owner. Option names and values are in each tool's own description; this guide covers how runs behave and how to work with them.

## Which tool

| Need | Tool |
|---|---|
| build, test, format, packages, EF Core, run a project | `cmd_dotnet` |
| git | `cmd_git` |
| ports, stop a program, build servers, delete bin/obj | `cmd_proc` |
| anything none of those can do | `cmd_shell` (last resort) |
| follow or inspect a run | `cmd_wait`, `cmd_output`, `cmd_list`, `cmd_stop` |

Prefer a typed tool: it is checked and can run without waiting for the owner. Give `purpose` as one sentence; the owner reads it before approving.

## How a run behaves

- Each operation has a gate the owner chose: Off (refused), Ask (waits for approval) or Auto (runs at once). The result says which. You cannot approve, deny or change a gate.
- A tool call lasts at most 60 seconds, so every run is detached and returns a run id. `waitSeconds` is at most about 45; when the run is still going, call `cmd_wait(runId)` again until it ends.
- Waiting for approval means nothing has run. Tell the user to approve it in the Waymark admin UI (Console page), then `cmd_wait`. An undecided request is denied after about 10 minutes. After a denial do not resend the same command; ask the user why.
- `cmd_output(runId)` shows the plain-text output, last lines by default. `grep` finds lines; `fromLine` with `lines` (up to 500) pages through it. A build prints thousands of lines, so read the errors in the result first.
- `cmd_list(workspace?, limit up to 30)` shows recent runs with their ids and status.
- `cmd_stop(runId)` ends a run that waits, is queued or is running (a `dotnet run`, say), with its whole process tree. It does nothing for a finished run.
- Operations that change files run one at a time per workspace; a second one is queued.

## cmd_dotnet

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

### Adding a package

Confirm with the user first, because it edits the .csproj. Use `package_add` with `project`, `package` and `version`: the latest stable that fits the project's target framework, preferring the version other projects here already pin (`nuget_packages` shows the latest). Then check the pinned version in `code_skeleton` and build.

## cmd_git

- Reading (status, diff, log, show, branches, stashes, remotes) is automatic. Changes ask the owner unless the owner set them to Auto. Nothing can force-push.
- `reset_hard`, `clean` and `branch_delete` are off unless the owner enables them. Use `clean` with `dryRun` first.
- Commit, push and branch changes only when the user asked for them.
- git is the undo for the file tools: `status` and `diff` show what changed.

## cmd_proc

Use it for a port that stays busy (`ports`, then `stop_port`), for files locked by compiler servers (`build_server`), or for stale build output (`clean_outputs` with `dryRun` first). `stop_port` only ends programs that run from the workspace or are dotnet programs, never Waymark.

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
