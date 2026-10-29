<p align="center">
  <img src="src/Waymark.Host/wwwroot/brand/waymark-logo.svg" alt="Waymark logo" width="96" height="96">
</p>

<h1 align="center">Waymark</h1>

![License: MIT](https://img.shields.io/badge/license-MIT-blue) ![Platform: Windows](https://img.shields.io/badge/platform-Windows-0078D4) ![.NET 10](https://img.shields.io/badge/.NET-10-512BD4) ![MCP server](https://img.shields.io/badge/MCP-server-black) ![Status: v0.1](https://img.shields.io/badge/status-v0.1-orange)

Local MCP server for developers working in C#, Python and TypeScript/JavaScript: token-efficient code, package and project context for claude.ai and Claude Code, over your own tunnel. Windows, SQLite, secrets stay on your machine.

> **Read this first.** Waymark gives an AI assistant real access to this computer: what it reads can reach your Claude chat, and what it runs acts as you. Changes and commands wait for your approval by default, approvals can only be given on this computer, and secrets in tool results are hidden before they leave. None of that is perfect, so read [docs/safety.md](docs/safety.md) before you install, and see [SECURITY.md](SECURITY.md) to report a problem. This is early software (v0.1), provided as is under the [MIT License](LICENSE).
>
> <img width="800" height="754" alt="image" src="https://github.com/user-attachments/assets/fb4312cc-4147-4892-aa36-41e63a7aea57" />

<img width="798" height="217" alt="image" src="https://github.com/user-attachments/assets/f3a9f039-6bac-4fae-9e9c-42412f3b049f" />



## What it does

| | |
|---|---|
| **Real package APIs** | `pkg_find` and `pkg_expand` show exact signatures from the package at the version your project pins: NuGet (the compiled package), PyPI (the wheel, with its stub package) and npm (declaration files or `@types`). A member that was not confirmed there does not exist. |
| **Workspace structure** | `code_skeleton` gives an overview that fits any workspace size: folders open level by level, the rest are summarised in one line each and opened on demand with `path`. Package pins from your lock files, SDK and target framework come with it. |
| **Your code, in three languages** | `code_search`, `code_outline`, `code_method`, `code_callers` and `code_types` read a syntax index (Roslyn for C#, tree-sitter for Python, TypeScript and JavaScript; no source text stored) that a file watcher keeps current. |
| **Text in any file** | `code_grep` finds literal text in any readable file, read from disk on every call, so it is never stale. `code_read` reads files and line ranges. |
| **Edit and build** | `file_edit_lines` and `file_write` change files (off until you switch them on) and can copy instead of retype: lines, whole files or whole folder trees, between workspaces, with namespace/identifier renames, so migrations cost almost no output tokens; `cmd_project` (build, test, install, type-check and run for .NET, Python and Node projects), `cmd_git`, `cmd_proc` and `cmd_shell` run commands, each operation set to Off, Ask or Auto. |
| **Safety** | Approvals only on this computer, never by Claude. Secrets are hidden in tool output, every call is audited, and **Pause all** stops every tool at once. There is no sandbox: read the note above. |

## Install

### Before you start

- Windows 10 or 11 (Windows only; macOS and Linux are not supported).
- git, to get the code and to update it later.
- An internet connection for the first build.
- Nothing else for Waymark itself: the installer adds the .NET 10 SDK if you do not have it, using `winget` (included in current Windows 10 and 11). Without winget it tells you where to download the SDK by hand. Windows may ask for permission once while it installs. For claude.ai you also need an ngrok account (see "Use it").
- Ports 47100 and 47101 free on this computer (they are the defaults; see [docs/install.md](docs/install.md) to change them).

### Steps

1. Open a terminal and get the code:

   ```powershell
   git clone https://github.com/patilprashant6792-official/waymark-local.git
   cd waymark-local
   ```

2. Run the installer. Type it in the terminal, or double-click `install.cmd` in the folder:

   ```bat
   install.cmd
   ```

3. Wait. The first run takes a minute or two (longer if the SDK has to be installed). It prints each step as it goes.
4. When it finishes, your browser opens the Waymark admin page at <http://localhost:47100>. If it does not, open that address yourself.

What the installer did for you:

| | |
|---|---|
| Program | built into `%LOCALAPPDATA%\Waymark\app` |
| Start | Waymark starts by itself, hidden, every time you sign in to Windows |
| Start menu | a **Waymark** shortcut that starts it if needed and opens the admin page |
| Your data | database, keys and secrets in `%LOCALAPPDATA%\Waymark`, separate from the program |

If the build fails, nothing is changed and an earlier install keeps working. If the page does not open, see [Troubleshooting](docs/install.md#troubleshooting) and look at the last lines of the log the installer prints.

### Day to day

- Waymark is already running after you sign in. Use the Start menu shortcut **Waymark** to open the admin page at any time.
- To stop it, in PowerShell: `& "$env:LOCALAPPDATA\Waymark\waymark.ps1" stop` (in Command Prompt: `powershell -File "%LOCALAPPDATA%\Waymark\waymark.ps1" stop`). To start it again, use the Start menu shortcut.
- Admin page: <http://localhost:47100> (this computer only).

### Update and remove

```text
update.cmd                 gets the latest version and installs it; your data is not touched
uninstall.cmd              removes Waymark; your data stays
uninstall.cmd -RemoveData  removes the data too (database, keys and secrets; asks you to type DELETE)
```

`update.cmd` runs `git pull --ff-only` in your clone, so keep the clone where it is. If you changed files in it, commit or stash them first or the pull is refused. To reinstall the current code without pulling, run `install.cmd` again.

## Use it

1. Open **Workspaces** and add the folder of a project Claude may read.
2. Connect Claude:
   - **Claude Code**, on this computer, no tunnel needed: `claude mcp add --transport http waymark http://localhost:47101/mcp`. Waymark shows an approval card on **Approvals**; check the code and approve. See [docs/auth-and-setup.md](docs/auth-and-setup.md).
   - **claude.ai**: needs a public address. Open **Setup**, enter your ngrok address and authtoken, and use the connector address it shows. See [docs/auth-and-setup.md](docs/auth-and-setup.md).
3. Watch what happens on **Live** and **Console**. **Pause all** in the header stops every tool at once.

## Before you connect Claude

Waymark lets Claude read your code and, when you allow it, run commands on your computer. Running tests, `dotnet run`, `pytest`, `npm run`, EF commands and the shell can all execute code that can read your files, so they ask for your approval by default. Waymark hides secrets it recognises in tool output, but detection is shape-based and not a guarantee. The **Safety** page in the admin UI lists every risky operation and every redaction. If a secret ever reaches a chat, rotate it and delete the chat. Details: [docs/safety.md](docs/safety.md).

## More

- [docs/install.md](docs/install.md): what the installer does, where things live, ports, the start log, running from source, troubleshooting.
- [docs/auth-and-setup.md](docs/auth-and-setup.md): how Claude connects and how the tunnel is set up.
- [docs/error-handling.md](docs/error-handling.md) and [docs/live-log.md](docs/live-log.md): what happens when something fails, and what the Live page shows.
- [docs/safety.md](docs/safety.md) and [docs/commands-policy.md](docs/commands-policy.md): the risks, what Waymark hides, and which operations ask first.
- [docs/guides.md](docs/guides.md): how Claude learns to use each tool group, and the host prompt where you add your own instructions.

## Try it and tell me what you think

Waymark is early, and honest feedback is what improves it most. If you work in .NET, please try it once on a real project, then tell me what worked, what broke and what is missing: open an [issue](https://github.com/patilprashant6792-official/waymark-local/issues), or see [CONTRIBUTING.md](CONTRIBUTING.md). Found a way past the secret hiding or the approvals? Please report it privately, as described in [SECURITY.md](SECURITY.md).

## License

Waymark is released under the [MIT License](LICENSE). Waymark gives an AI access to your computer; read [docs/safety.md](docs/safety.md) before you use it. The software is provided as is, without warranty.
