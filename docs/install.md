# Installing Waymark

Waymark is a local program for Windows and macOS. It is built on your own computer from this repository, runs as you, and keeps everything it stores on your computer. The sections below describe Windows first; [macOS](#macos) has its own section with the differences.

## What you need

- Windows 10 or 11 with `winget` (included in Windows 11 and in current Windows 10 through the App Installer). Without winget the script tells you where to download the .NET 10 SDK by hand, and ngrok must be installed by hand too.
- git, to get the repository and to update.
- Internet access for the first build (NuGet packages) and, if you use claude.ai, for the tunnel.
- Optional, only for the matching project actions: Python 3 (with [uv](https://docs.astral.sh/uv/) for lock-file installs) for `py_*`, and Node.js with npm for `node_*`. Waymark finds them on PATH and in their usual install folders; `py_info` and `node_info` show what it found.

The .NET 10 SDK is installed by the script when it is missing. No administrator rights are needed for Waymark itself; `winget` may ask for them when it installs the SDK.

## What `install.cmd` does

1. Looks for the .NET 10 SDK and installs it with winget if needed.
2. Builds `Waymark.Host` in Release mode into a side folder. Warnings are not treated as errors in this build, so a newer SDK cannot stop the install. If the build fails, the installed version (if any) is left exactly as it was.
3. Stops a running copy, swaps the new build in, and copies `waymark.ps1` next to your data so the Start menu and the sign-in task do not depend on where you cloned the repository.
4. Makes Waymark start when you sign in: a scheduled task named **Waymark** that runs as you, hidden, and restarts the program if it fails (up to 3 times, a minute apart). If Windows refuses to create the task, a shortcut in your Startup folder does the same without the restart.
5. Adds the Start menu shortcut **Waymark**, starts the program, waits until the admin page answers, and opens it.

Running `install.cmd` again is safe: it is the same as an update without `git pull`.

## Where things live

| What | Where |
|---|---|
| The program | `%LOCALAPPDATA%\Waymark\app` (replaced by every install and update) |
| Your data: database, keys, secrets, command output | `%LOCALAPPDATA%\Waymark` (never touched by update) |
| The script copy used by the task and the shortcut | `%LOCALAPPDATA%\Waymark\waymark.ps1` |
| Start log of the last two runs | `%LOCALAPPDATA%\Waymark\logs` |
| Admin page (this computer only) | <http://localhost:47100> |
| Tool and sign-in address (what the tunnel forwards to) | `http://localhost:47101` |

The ports can be changed with the user environment variables `Waymark__AdminPort` and `Waymark__McpPort`; the script reads the same variables. The data folder can be moved with `Waymark__DataDirectory`.

## Starting and stopping

Waymark starts by itself when you sign in. Otherwise use the Start menu shortcut **Waymark**: it starts the program if it is not running and opens the admin page. From a terminal the installed script accepts `start`, `stop`, `open`, `status`, `update` and `uninstall`. In PowerShell: `& "$env:LOCALAPPDATA\Waymark\waymark.ps1" status`. In Command Prompt: `powershell -File "%LOCALAPPDATA%\Waymark\waymark.ps1" status`.

## The start log

The program has no window, so a failed start would leave no trace. The sign-in task therefore saves what the program prints to `logs\host.log` and `logs\host.err.log`, and keeps the previous run as `host.previous.log` and `host.err.previous.log`. Only warnings, errors and the start-up lines are written, so the files stay small. This is separate from the Live page, which keeps nothing on disk.

## Updating

`update.cmd` runs `git pull --ff-only` in your clone and installs the result. If you changed files in the clone, git may refuse the pull; commit or stash your changes and run it again. Your data stays as it is, and database migrations run when the new version starts.

## Removing

`uninstall.cmd` stops Waymark and removes the program, the sign-in task and the shortcuts. Your data stays. `uninstall.cmd -RemoveData` also deletes the data folder after you type `DELETE`: that destroys the database, the encryption keys and every stored secret, and it cannot be undone.

## Running from source instead

For development you do not need any of this: `dotnet run --project src/Waymark.Host` starts the same program with a console window, using the same data folder. Stop the installed copy first (`stop`), because both use the same ports.

## Troubleshooting

| Symptom | What to do |
|---|---|
| "Port 47100 is already used by ..." | Another program (or a copy started from Visual Studio) holds the port. Close it, or set `Waymark__AdminPort` and `Waymark__McpPort` and install again. |
| "The .NET 10 SDK was installed but is not visible yet" | Close the window, open a new one and run `install.cmd` again. |
| The build fails | Run `dotnet --version` (it must report 10.0.x). The message above the failure names the file. Nothing was changed, so the installed version still works. |
| The admin page does not open after sign-in | Run the `status` command (see "Starting and stopping"), then look at `logs\host.err.log`. |
| "running scripts is disabled" | Use the `.cmd` files; they start PowerShell with the policy relaxed for that one run only. |

## Good to know

- Waymark listens on this computer only. Only the tool and sign-in port is ever forwarded by a tunnel; the admin page is never exposed.
- It runs as your account, not as a Windows service or a macOS daemon, because its keys (protected by your Windows account or your login Keychain), your project folders and your git credentials belong to you.
- Windows and macOS are supported. Linux is not.
- There is no installer file and no signing: the program is built on your own computer, so neither Windows nor macOS shows a download warning.

## macOS

### What you need

- macOS 14 (Sonoma) or later, on Apple silicon or Intel.
- git. If it is missing, macOS offers to install the Command Line Tools the first time you run `git`; accept, or run `xcode-select --install`.
- Internet access for the first build and, if you use claude.ai, for the tunnel.
- Optional: [Homebrew](https://brew.sh), which Waymark uses to install ngrok for you. Without it, install ngrok from <https://ngrok.com/downloads/mac-os>.
- Optional, only for the matching project actions: Python 3 and Node.js, as on Windows. Waymark looks on your PATH and in the usual Homebrew, pyenv, python.org, nvm and Volta folders.

### What `install.sh` does

1. Looks for the .NET 10 SDK and, if it is missing, installs it into `~/.dotnet` with Microsoft's `dotnet-install.sh` (no administrator rights).
2. Builds `Waymark.Host` in Release mode into a side folder, exactly as on Windows: a failed build changes nothing.
3. Stops a running copy (and the ngrok it started), swaps the new build in, and copies `waymark.sh` next to your data.
4. Writes a LaunchAgent, `~/Library/LaunchAgents/com.waymark.host.plist`, that starts Waymark when you sign in and starts it again after a crash (at most once a minute). The LaunchAgent gets the PATH of your own login shell, so Waymark finds what your terminal finds (Homebrew, nvm, pyenv).
5. Adds a small **Waymark** app to `~/Applications` that opens the admin page, starts the program, waits until the admin page answers, and opens it.

Run it with `bash install.sh`; running it again is safe, as on Windows.

### Where things live

| What | Where |
|---|---|
| The program | `~/Library/Application Support/Waymark/app` (replaced by every install and update) |
| Your data: database (with the encrypted key ring), secrets, command output | `~/Library/Application Support/Waymark`, readable by you only (never touched by update) |
| The key that opens the key ring | your login Keychain, item `waymark-data-protection` |
| The script copy used by the LaunchAgent and the app | `~/Library/Application Support/Waymark/waymark.sh` |
| Start log of the last two runs | `~/Library/Application Support/Waymark/logs` |
| Admin page and tool address | <http://localhost:47100> and `http://localhost:47101`, as on Windows |

The port and data folder variables (`Waymark__AdminPort`, `Waymark__McpPort`, `Waymark__DataDirectory`) work the same way. Set them in your shell before `install.sh` runs; the installer copies them into the LaunchAgent.

### How your secrets are protected

On Windows the Data Protection keys are files protected by DPAPI. On macOS they are rows in the database, each encrypted (AES-256-GCM) with a master key that lives in your login Keychain, which macOS encrypts with your login password. A copied data folder or backup therefore opens nothing without your Mac account. The Keychain item is created and read through Apple's own `security` tool, so updating Waymark never brings up a Keychain permission prompt. If the Keychain is locked or the item was deleted, the Live page says so, and secrets and the ngrok token must be entered again only if the item is really gone.

### Starting and stopping

The installed script accepts `start`, `stop`, `open`, `status`, `update` and `uninstall`:

```bash
bash ~/Library/Application\ Support/Waymark/waymark.sh status
```

`stop` unloads the LaunchAgent until you start it again or sign in again.

### Updating and removing

`bash update.sh` and `bash uninstall.sh` work as their Windows counterparts. `bash uninstall.sh --remove-data` also deletes the data folder and the master key in the login Keychain after you type `DELETE`.

### Troubleshooting

| Symptom | What to do |
|---|---|
| A workspace in Documents, Desktop, Downloads or iCloud Drive shows no files, or "Operation not permitted" | macOS privacy protection. Move the project (to `~/source`, say), or give `Waymark.Host` Full Disk Access in System Settings > Privacy & Security, then restart Waymark. |
| "The Waymark master key in the macOS login Keychain is not available" on the Live page | Sign in at the Mac so the login Keychain is unlocked, then run the `start` command. If it persists, open Keychain Access and look for `waymark-data-protection`. |
| git or python3 "not found" although `/usr/bin` has them | Those are placeholders until the Command Line Tools are installed: run `xcode-select --install`. |
| A tool is found in Terminal but not by Waymark | The LaunchAgent took your PATH when you installed. Run `bash install.sh` again after changing your shell profile. |
| "Port 47100 is already used by ..." | As on Windows: close the other program or set other ports, and install again. |
