# Waymark: auth flow and tunnel setup

How Claude gets from "add custom connector" to a working, approved connection, what is built, and what is still open.

## The two listeners

| Listener | Default port | Serves | Reachable from |
|---|---|---|---|
| Admin | 47100 | Blazor UI (Dashboard, Setup, Approvals) | this computer only, Host header must be loopback |
| MCP | 47101 | OAuth endpoints, discovery documents, `/mcp` | this computer, and the internet through the tunnel |

The tunnel forwards to the MCP port only: `ngrok http 47101`. `ListenerGuard` returns 404 for admin paths on the MCP port and for OAuth/MCP paths on the admin port, so a mistake in routing cannot expose the UI. Ports can be changed with `Waymark:AdminPort` and `Waymark:McpPort`.

## The flow

1. Claude calls `POST /mcp` without a credential and gets `401` with `WWW-Authenticate: Bearer resource_metadata="<base>/.well-known/oauth-protected-resource"`.
2. Claude reads the protected-resource and authorization-server documents. `<base>` is the public address you saved in Setup, or `http://localhost:<port>` when the request itself arrived on localhost (so Claude Code can be tested without a tunnel).
3. Claude registers itself at `POST /register` (public client, redirect address must be on the allowlist).
4. Claude opens `GET /authorize` in your browser. Waymark validates it (client, exact redirect address, PKCE S256, scope, resource), stores a pending approval, and shows a pairing code. The page reloads itself every two seconds.
5. The admin UI shows an approval card with the same code. You approve or deny.
6. The next reload mints a one-time code (only its hash is stored) and sends the browser back to Claude with `code` and `state`, or with `error=access_denied`.
7. Claude exchanges the code at `POST /token` (form-encoded, PKCE verifier) for a 60-minute access token and a 30-day refresh token. Refresh tokens rotate; presenting a used one revokes the whole family.
8. Claude calls `/mcp` with the access token.

## Redirect allowlist

`https://claude.ai/api/mcp/auth_callback` exactly, plus `http://localhost:{any port}/callback` and `http://127.0.0.1:{any port}/callback` for Claude Code. Anything else is refused at registration.

## Status

| Part | State |
|---|---|
| Database, migrations, stores for settings, clients, approvals, audit | done |
| Listeners, guard, security headers, event bus, public address, rate limits | done |
| Discovery documents, instance probe, `/register`, `/authorize`, `/authorize/wait` | done |
| Admin UI: Dashboard, Approvals, Audit, Live, Workspaces, Tools, Access keys, Setup (installs ngrok, starts and stops the tunnel, verifies the address). The Vault and Connectors pages exist but are hidden from the menu for now | done |
| Grant store (codes, access and refresh pairs), access-key store, bearer check, `/token`, `/revoke`, `/mcp` with `waymark_ping` | done, built but not yet tried against Claude |

Migration `002_grants.sql` recreates the code, token and API-key tables with the columns the stores use. File names containing token, credential, apikey or secret are refused by the dotnet-mcp-server connector, so these pieces are called Grant and AccessKey.

## Try it by hand

1. Run `Waymark.Host`. The console prints the admin address and the MCP address.
2. Open `http://localhost:47100`, then Setup. Enter your ngrok address and authtoken and choose Save and start tunnel: Waymark installs and starts ngrok itself, then reports each check as passed or failed. If ngrok cannot start, run the `ngrok http 47101` command shown on the page and choose Check again. The connector address unlocks once the check passes.
3. Check the documents: open `<your address>/.well-known/oauth-authorization-server` in a browser. It should show your address as the issuer.
4. With a client registered by Claude, the Approvals page shows the card and, afterwards, the client and the decision history.

## Decisions worth knowing

- A small bearer handler reads the saved public address on every request, instead of the MCP SDK's authentication handler, whose options take one fixed metadata address.
- Approvals are stored (not memory-only), so they show in the UI history and survive until the restart sweep expires them.
- The pairing page needs no script, which keeps its content security policy at `default-src 'none'`.
- The admin content security policy is in report-only mode until it has been checked in a real browser.
- `/api/tunnels` on the ngrok agent is marked deprecated by ngrok, so "Detect from ngrok" is best effort and pasting always works.
