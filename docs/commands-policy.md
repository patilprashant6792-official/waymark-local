# Commands policy

How Claude may run commands on this computer, and how the owner stays in control. Code: `Waymark.Core/Commands` (contracts, `CommandCatalog`), `Waymark.Modules.Commands` (engine), `Waymark.Host` (`/console`, `/commands`, `CommandAdminService`).

## Tools

| Tool | Tier | What it does |
|---|---|---|
| `cmd_project` | 1, typed | .NET: build, rebuild, clean, restore, test, format, info, list, new, sln, reference, package add/remove, tool restore, EF Core, run. Python (`py_*`): info, list, venv, install, test, lint, format, typecheck, run, package add/remove |
| `cmd_git` | 1, typed | status, diff, log, show, branches, stashes, remotes, add, commit, branch, stash, restore, pull, push, and (off by default) reset hard, clean, branch delete |
| `cmd_proc` | 1, typed | list ports, stop the program on a port, stop build servers, delete `bin` and `obj` |
| `cmd_shell` | 2, any script | PowerShell, cmd or bash. **Every script waits for the owner.** With `terminal`, the script keeps running in a terminal |
| `cmd_terminal` | terminals | list, read, send (type a line), rerun, restart, stop, clear, close: work with programs that keep running (see Terminals) |
| `cmd_wait`, `cmd_output`, `cmd_list`, `cmd_stop` | read | follow, read, list and end runs |

The tools that start something carry `ToolGate.Confirm`, so they follow the Tools page like the file tools. A run is detached and returns a run id, because one tool call is limited to 60 seconds (`Waymark:Tools:CallTimeoutSeconds`); `waitSeconds` is at most about 45, then `cmd_wait` continues.

## Gates: Off, Ask, Auto

Each operation (`CommandCatalog`) has a gate:

- **Off**: refused.
- **Ask**: the run waits on an approval card (Dashboard and Console) until the owner decides.
- **Auto**: runs at once.

The gate that applies is the first of: the owner's choice for this workspace, the owner's choice for all workspaces (workspace id 0), the catalog default. The result is capped by the operation's `MaxGate`. `shell.run` has `MaxGate = Ask`, so a script can never run on its own.

| Family | Auto by default | Ask by default | Off by default |
|---|---|---|---|
| dotnet | build, rebuild, clean, restore, format check, info, list | test, EF read, format, new, sln, reference, package add/remove, tool restore, EF migrations add/remove, EF database update, run | - |
| python | info, lint | list, venv, install, test, format, typecheck, run, package add/remove | - |
| git | read (status, diff, log, show, blame, branches, stashes) | add, commit, branch, stash, restore, pull, push | reset hard, clean, branch delete |
| processes | list ports, stop build servers | stop the program on a port, delete bin and obj | - |
| shell | - | system shell | - |
| terminals | type into a running app | type into a running script (capped at Ask) | - |

Choices are made on `/commands` (per workspace or for all) and each change writes a Config audit row: `config.command_gate`, `config.command_gate_reset`.

## Drift gate

Building, testing and many git commands load files that can run code: MSBuild props and targets, `Directory.Build.*`, `global.json`, `nuget.config`, solution files, git hooks. Operations marked `DriftSensitive` therefore compare a SHA-256 fingerprint of those files (`BuildFingerprint`) with the state the owner last approved.

- No approved state yet: an Auto operation asks once.
- Files changed since: an Auto operation asks again and the card names the changed files.
- The check lowers Auto to Ask only. Off stays Off.
- It is evaluated again just before the process starts, so a file changed while the run waited is caught.
- Approving a command, and commands that edit those files themselves, store the new fingerprint. **Forget** on `/commands` clears it (`config.command_trust_reset`).

## Approval is local only

`ICommandRuns.DecideAsync` is called only by the admin UI, which listens on localhost. No MCP tool can approve, deny or change a gate, and the tunnel serves only OAuth and MCP. The owner may edit a shell script before approving; the edited text is what runs and is stored (`original_command` keeps what Claude asked for). A request nobody decides is denied after `ApprovalMinutes`.

Before a script, the card shows plain-language hints (`RiskHints`): deletes files recursively, downloads from the internet, runs downloaded or encoded code, changes the registry or services, runs as administrator, formats a disk, forces a git push or discards changes, touches keys or tokens, ends programs, connects to another computer, touches system folders. They help reading; they do not filter. A script that matches nothing is still shown in full.

## How a typed command is started

- No shell. The executable is resolved to an absolute path from `PATH`, arguments go in `ArgumentList`, standard input is closed.
- Every option is checked (`RecipeOptions`); paths must stay inside the workspace (`IPathGuard`); unknown option keys are refused.
- The environment is an allow-list (`ChildEnvironment`): Waymark's own variables never reach the child. Prompts are switched off.
- On Windows the process runs in a Job Object, so ending a run ends the whole tree. macOS has no Job Objects: ending a run ends the process tree as it is at that moment, so a child that detached itself (double-forked, or reparented to launchd) can outlive the run. `MSBUILDDISABLENODEREUSE` is set for every run and builds pass `-nodeReuse:false -p:UseSharedCompilation=false`, which keeps .NET build servers from staying behind; `cmd_proc build_server` ends any that do.
- Git: no force push, no pager, no external diff or textconv, paths after `--`.
- Operations that change files run one at a time per workspace.


## Terminals: programs that keep running

A dev server, `dotnet watch`, `npm run dev` or `python -m app` does not end, so it cannot be a normal run that Claude waits for. Such commands start in a **terminal**: a named slot per workspace (or System) that runs one program at a time, like a tab in an IDE's terminal panel. Code: `Waymark.Modules.Commands/Terminals` (`TerminalCoordinator`, `ReadyDetector`, `HealthWatch`, `TerminalLog`, `TerminalLifetimeService`), the `terminals` table (migration 027) and `command_runs.terminal_id`.

- **Which commands.** `dotnet run`, `dotnet watch`, `py_run` and `node_run` always start in a terminal; the name defaults to the project (`api`, `web`), or Claude passes `terminal`. `cmd_shell` starts in one when Claude passes `terminal` (and optionally `readyPattern`, `readyPort`).
- **Each start is a normal run.** Policy, approval, drift and audit apply as for any command. `dotnet run` first builds (holding the workspace's turn), then runs `--no-build`.
- **Three phases.** Prepare (the build) holds the workspace's turn. Start holds the turn (when the operation changes files) and a run slot until the program is ready, exits or `TerminalStartLockSeconds` passes. Live holds nothing and has no time limit: other commands run beside it.
- **Ready.** First a known line (`Now listening on`, Vite's `Local:`, uvicorn's `Uvicorn running on` and others), then a port the program's process tree listens on, and for a shell script, a few quiet seconds. The tool call returns when ready, with the address and the last lines.
- **Health.** While it runs: app down and back up (for watchers), new error lines, and a line that looks like a prompt. Claude sees changes as a 🖥 line after its next tool call, and in the workspace guide's Terminals block.
- **Input.** Standard input stays open (pipes, no pseudo-terminal). `terminal.input` (typing into a typed command) is Auto by default; `terminal.shell_input` (typing into a shell script, which can be any command) is capped at Ask, and each line waits on an approval card. Every line typed is shown in amber in the Console and kept in Audit.
- **Rerun.** The same command starts again in the same terminal. An approval of an unchanged typed command is reused for `TerminalApprovalDays` when the project files are unchanged; shell scripts always ask. **Forget the terminal approvals** on a workspace's Commands page clears them (`terminal.approvals_forget`).
- **Stop.** Ctrl+C (SIGINT), then terminate, then kill the tree; on Windows the Job Object ends the tree. Claude stops with `cmd_terminal stop` or `cmd_stop`; the owner stops from the header, the Console, the workspace card or its Terminals page. The run records who stopped it (`stopped_by`).
- **Waymark restarts.** On shutdown every terminal is stopped gracefully. On start, programs left from a crash are found by process id and start time and ended; their terminals show "Ended: Waymark restarted". Switching a workspace off or removing it stops its terminals.
- **Limits.** At most `MaxLiveTerminals` programs at once. The log streams to `run-{id}.log` and rotates at `TerminalLogMaxMegabytes`, keeping `TerminalLogFiles` files. Terminals unused for 30 days are removed by maintenance.
- **The owner only watches and stops.** The Console shows a terminal as a read-only view; only Claude starts programs and types into them.
## Output

- `/console` shows ANSI output through Waymark's own renderer (`AnsiBuffer`, no xterm). The terminal is dark in both themes so the 16 colours keep their contrast; colours beyond the first 16 become inline styles.
- Claude receives plain text without colour codes, with compiler errors and warnings listed separately.
- The full output is stored in `<DataDir>/runs/run-{id}.log`; the database keeps only its size and path.

## Settings

| Key | Default | Meaning |
|---|---|---|
| `Waymark:Commands:ApprovalMinutes` | 10 | how long an Ask waits before it is denied |
| `Waymark:Commands:MaxParallelRuns` | 4 | runs at once |
| `Waymark:Commands:RetentionDays` | 30 | how long finished runs and their logs are kept |
| `Waymark:Commands:MaxLiveTerminals` | 8 | programs running in terminals at once |
| `Waymark:Commands:TerminalStartLockSeconds` | 180 | the longest a starting terminal program holds the workspace's turn and a run slot |
| `Waymark:Commands:TerminalLogMaxMegabytes` | 20 | size at which a terminal's log rotates |
| `Waymark:Commands:TerminalLogFiles` | 3 | log files kept per terminal run, the current one included |
| `Waymark:Commands:TerminalApprovalDays` | 7 | how long the approval of an unchanged terminal command is reused |
| `Waymark:DataDirectory` | `%LOCALAPPDATA%\Waymark` | holds the database and the `runs` folder |

## Audit

Every decision writes `command.approve` or `command.deny`. Every stop writes who stopped the run. Every line typed into a terminal is audited. Every gate change and every trust reset writes a Config row (see above), and so does forgetting terminal approvals.

## Safety

Tests, `dotnet run` and EF start the project's own code, which can be code Claude has just written; no file fingerprint covers it, so they ask by default. Every operation has a risk tier (Reads, Changes, Runs code, Sends out) shown on `/commands` and on the approval card. Everything Claude receives is scrubbed for secrets and ends with a safety line; see `safety.md`. The console and the stored run log keep the raw output on this computer.

### Python (`cmd_project`, actions `py_*`)

Gates: `python.info` and `python.lint` Auto; list, venv, install, test, format, typecheck, run and package add/remove Ask. Test, run, typecheck and list start the project's own code (the interpreter of its virtual environment is a program inside the workspace, which the approval card shows); lint and format are drift-sensitive; install and package add send out.
Installs run no package code: wheels only (`uv --no-build`, `pip --only-binary :all:`), the project itself is never built, a requirements file with editable, path, URL or option lines (other than `--hash` and index settings) is refused, and `PIP_REQUIRE_VIRTUALENV` keeps pip out of the system Python. `ruff`, `uv`, `pyright` and `mypy` are found on PATH (and `~/.local/bin`, `~/.cargo/bin`), never in the workspace. The build fingerprint covers pyproject.toml, setup.py/cfg, lock files, requirements*.txt, conftest.py, noxfile.py and the tool settings. `.pypirc`, `pip.conf`, `.npmrc` and `.netrc` are blocked files. Poetry and PDM projects are not run yet.
