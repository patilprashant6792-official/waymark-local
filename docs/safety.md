# Safety

Waymark gives an AI agent real access to this computer: what it reads can reach your Claude chat, and what it runs acts as you. This page lists what limits the damage and, as important, what does not. The same text, shorter, is on the **Safety** page of the admin UI (`/safety`).

## What happens to every tool result

One class, `ToolResultGuard` (`Waymark.Host/Mcp`), is applied by `ToolPipeline` to every result, error messages included. A tool does not have to do anything.

1. **Hide.** Values saved in the Vault are replaced by `[secret:NAME]`. Then `SensitiveDataScanner` (`Waymark.Core/Security`) replaces secrets that have a recognisable shape with `[redacted:kind]`: private keys, AWS, GitHub, Slack, NuGet, Google and `sk-` keys, JWTs, `user:password@` in URLs, `Password=` in connection strings and command lines, JSON values whose key ends in password, secret, token, key ..., and `NAME_TOKEN=...` lines.
2. **Cut.** The output limit is applied after hiding, so a cut can never leave half a secret.
3. **Tell.** If something was hidden, a closing text block names the kinds and counts. The safety line (discard a secret you were shown, do not repeat it, advise the user to rotate it and delete the chat) is in the overview, and is added to every result only if `AppendFooter` is on.
4. **Record.** Each time something is hidden, one `exposure.redacted` row (category Config) is written: tool, client, kinds and counts, never a value. The Safety page lists them.

If the check itself fails, the result is withheld, not sent unchecked.

## Markers cannot be written back

A Confirm tool (file writes, commands) whose input contains a finished marker is refused, so a hidden secret never becomes text in a file. A source file that merely mentions the marker prefix is fine.

## Copies and secrets

`from` / `fromFolder` in `file_edit_lines` and `file_write` move real bytes on the server without the agent reading them, so the result guard never sees that text. `IContentInspector` (`Waymark.Core/Security`, implemented by `ContentInspector` in `Waymark.Host/Vault`) runs the same checks on the copied text: Vault values, then the credential shapes, and a finished marker. Any finding refuses that copy and names only the kinds. Both workspaces' access rules apply to the source and the target, links are refused, and blocked folders and secret-bearing files cannot be copied from or to. The result names where text came from (workspace, file, lines), never the text.
## Settings

| Key | Default | Meaning |
|---|---|---|
| `Waymark:Safety:RedactSecrets` | true | hide secrets in results |
| `Waymark:Safety:AppendFooter` | false | add the safety line to every result |
| `Waymark:Safety:FooterText` | built-in line | the safety line (at most 500 characters) |

The line is off by default because the overview (see [guides.md](guides.md)) already tells agents what to do. The note "Waymark hid ..." is still added whenever something is hidden. Switching the line on costs about 40 tokens per call.

## Risk tiers

`RiskTiers` (`Waymark.Core/Commands`) places every operation: **Reads**, **Changes**, **Runs code**, **Sends out**. Operations that start the project's own code (test, run, EF, scripts) ask by default and show a red note if set to Auto.

## What this does not do

- **No sandbox.** Code that runs (a test, the app, a script) can read your keys and use the network. The only protection is that it asks first.
- **Shape-based.** A secret in an unusual form is not recognised, and a recognised-looking value that is not a secret is hidden.
- **The safety line is advice** to the model, not enforcement.
- **The console and the stored run log** keep the raw output on this computer for 30 days. Only what is sent to Claude is scrubbed.
- **Build is automatic** by default. It runs MSBuild, analyzers and generators from your project files, which are fingerprinted; the source of a project-referenced analyzer is not.
- **Deleting the chat does not take anything back.** Rotate a secret that reached it.
