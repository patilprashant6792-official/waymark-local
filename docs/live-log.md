# Live log

A console-style view of what Waymark is doing, on the admin UI at `/live`. It shows four streams and errors, nothing else. Entries are held in memory only (the last 200 lines): nothing is written to disk, and a restart empties the view.

## What shows up

| Tab | What a line says | Where it comes from |
|---|---|---|
| Tools | `#1234 code_read  Waymark  ok  42 ms  relativeFilePath=docs/HANDOFF.md` | `ToolPipeline`, once per call. Long argument values are cut and say how much was left out; values with a secret-like name are hidden. `#1234` is the call's audit reference, with an **open** link to its details page |
| Watcher | `changed  Waymark  src/Foo.cs`, `watching`, `lost events` | `FileWatcherHost`. A burst shows its first 8 lines and then one summary line (`burst  Waymark  37 more changes`) |
| Auth | pairing, approval, revocation, and requests to `/authorize`, `/token`, `/revoke`, `/register` | `AuditStore` (Auth and Approval entries) and `RequestLoggingMiddleware`. Only the path is logged, never the query |
| Commands | `#12 waiting for you`, `approved`, `started`, the first 3 output lines, `succeeded  exit 0  0.4s` | `CommandCoordinator`. The full output stays in `/console` |
| Errors | every Warning and Error line of any tab, plus any error logged by any other part of Waymark or ASP.NET, including failures caught by the safety net (`docs/error-handling.md`) | the same sources; the tab shows a count |

Not shown: page loads of the admin UI, successful MCP requests (the tool line replaces them), configuration changes (see Audit), and everything logged with a plain `logger.LogInformation(...)`. Those still go to the console and any other provider.

Failed or timed-out commands and failed tool calls are errors; denied or stopped ones are warnings.

## Happening now

A strip at the top lists what is in flight: running tool calls and commands that wait, queue or run, with a counter. The header shows a chip with the same count on every page. An item disappears when the work ends; nothing about it is kept (`LiveActivity`, in memory).

## How it works

- `Waymark.Core/LiveFeed` holds the contract: `LogKind`, `LogEvents`, the `ActivityLogging` helpers, `LiveActivity` and `FailureReporter`. Modules reference Core only, so they log through these.
- `Waymark.Logging` is an ordinary logging provider (`builder.Logging.AddLiveLog()`). It is an allow-list: it keeps an entry only when it was made with one of the helpers, or when it is an error.
- Entries go into a ring of 200 (`LiveLogHub`) and are pushed to every open Live page. The oldest are overwritten. Clear empties the ring.
- A page that cannot keep up skips its oldest pending lines and says how many. The ring itself is not affected.
- Before an entry is kept, `LogScrubber` hides bearer values, Waymark token prefixes (`sma_`, `smr_`, `smk_`), values of `code`, `p`, `state`, `token`, `password` and similar keys, JSON secrets, and any field whose name ends in `password`, `secret`, `token`, `authorization`, `verifier` or `apikey`. A message is cut at 300 characters and an exception at 2,000.
- The page is a dark terminal like `/console`. It follows new lines while you are at the bottom and stays put once you scroll up.

## For code that wants a line on the Live page

```csharp
logger.LogToolCall(LogLevel.Information, "code_search", "Waymark", "ok", elapsedMs, "query=Scrub");
logger.LogWatch(LogLevel.Information, "changed", "Waymark", "src/Foo.cs");
logger.LogAuth(LogLevel.Warning, "approval.denied", "admin-ui", clientId, "denied");
logger.LogCommand(LogLevel.Information, runId, "started", "Waymark", string.Empty);

using var running = liveActivity.Begin(LogKind.Tool, "code_search"); // shown under "happening now" until disposed

FailureReporter.Report(exception, "Reading the output of a helper program"); // for a failure that is caught and skipped
```

Categories starting with `Waymark` are allowed down to Information on this provider (`Program.cs`); a Debug line is never kept.

## The audit trail is separate

The `audit_entries` table is a stored record of security-relevant actions. It is not part of the live view, which only mirrors new Auth and Approval entries as they are written.

A tool line carries the audit reference of its call (`#1234 code_read  Waymark ...`, structured field `CallId`) and an **open** link to the call's details page, where the input is shown and the output is loaded on request. The reference exists because the audit row is written before the line; if the row could not be written, the line has no reference and no link.
