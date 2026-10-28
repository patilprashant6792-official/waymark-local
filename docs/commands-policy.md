# Commands policy

How Claude may run commands on this computer, and how the owner stays in control. Code: `Waymark.Core/Commands` (contracts, `CommandCatalog`), `Waymark.Modules.Commands` (engine), `Waymark.Host` (`/console`, `/commands`, `CommandAdminService`).

## Tools

| Tool | Tier | What it does |
|---|---|---|
| `cmd_dotnet` | 1, typed | build, rebuild, clean, restore, test, format, info, list, new, sln, reference, package add/remove, tool restore, EF Core, run |
| `cmd_git` | 1, typed | status, diff, log, show, branches, stashes, remotes, add, commit, branch, stash, restore, pull, push, and (off by default) reset hard, clean, branch delete |
| `cmd_proc` | 1, typed | list ports, stop the program on a port, stop build servers, delete `bin` and `obj` |
| `cmd_shell` | 2, any script | PowerShell, cmd or bash. **Every script waits for the owner.** |
| `cmd_wait`, `cmd_output`, `cmd_list`, `cmd_stop` | read | follow, read, list and end runs |

The four tools that start something carry `ToolGate.Confirm`, so they follow the Tools page like the file tools. A run is detached and returns a run id, because one tool call is limited to 60 seconds (`Waymark:Tools:CallTimeoutSeconds`); `waitSeconds` is at most about 45, then `cmd_wait` continues.

## Gates: Off, Ask, Auto

Each operation (`CommandCatalog`) has a gate:

- **Off**: refused.
- **Ask**: the run waits on an approval card (Dashboard and Console) until the owner decides.
- **Auto**: runs at once.

The gate that applies is the first of: the owner's choice for this workspace, the owner's choice for all workspaces (workspace id 0), the catalog default. The result is capped by the operation's `MaxGate`. `shell.run` has `MaxGate = Ask`, so a script can never run on its own.

| Family | Auto by default | Ask by default | Off by default |
|---|---|---|---|
| dotnet | build, rebuild, clean, restore, format check, info, list | test, EF read, format, new, sln, reference, package add/remove, tool restore, EF migrations add/remove, EF database update, run | - |
| git | read (status, diff, log, show, blame, branches, stashes) | add, commit, branch, stash, restore, pull, push | reset hard, clean, branch delete |
| processes | list ports, stop build servers | stop the program on a port, delete bin and obj | - |
| shell | - | system shell | - |

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
- On Windows the process runs in a Job Object, so ending a run ends the whole tree.
- Git: no force push, no pager, no external diff or textconv, paths after `--`.
- Operations that change files run one at a time per workspace.

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
| `Waymark:DataDirectory` | `%LOCALAPPDATA%\Waymark` | holds the database and the `runs` folder |

## Audit

Every decision writes `command.approve` or `command.deny`. Every gate change and every trust reset writes a Config row (see above).

## Safety

Tests, `dotnet run` and EF start the project's own code, which can be code Claude has just written; no file fingerprint covers it, so they ask by default. Every operation has a risk tier (Reads, Changes, Runs code, Sends out) shown on `/commands` and on the approval card. Everything Claude receives is scrubbed for secrets and ends with a safety line; see `safety.md`. The console and the stored run log keep the raw output on this computer.
