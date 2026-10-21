# Guides and the host prompt

Waymark teaches a connected agent in two layers, both delivered through MCP tool results, so they work with or without a skill.

## The overview (host prompt)

A short text, written by Waymark, that an agent loads once per conversation with `waymark_guide`. The server instructions tell it to, and the tool is marked `anthropic/alwaysLoad` for clients that support it. It holds:

- the four groups (code, nuget, files, commands) and how to ask for a group's guide;
- a few cross-cutting rules: a refusal is a decision, a missing tool is switched off, never write a `[redacted:...]` marker into a file, what to do if a secret shows up, ask before anything hard to undo;
- **your instructions**, under "Instructions from the owner" (edit them on the **Host prompt** page; up to 40,000 characters; secrets are refused because every agent receives the text);
- the **live setup**: tools switched off, workspaces, and the command operations that ask first or are refused.

The template is `src/Waymark.Host/Guide.md` (markers `{{owner}}` and `{{setup}}`). Your text is stored in the `tool_guides` table under the key `host`. The hash covers the template and your text, not the live setup, so a settings change does not re-announce the overview.

## Group guides

One built-in guide per group (`Guide.md` in each module, embedded in the assembly). They cover the tools, the order of work, long runs and hand-offs, project scripts and restarting Waymark itself. They are not editable; they change with Waymark.

An agent gets one by adding `guide: true` to its first call to that group. The first group guide also brings the overview if the agent has not loaded it.

## Notices

An agent that never asked is told once, in a tool result, that a guide exists. After you save new instructions, every agent that holds the old overview is told once on its next call. Delivery is tracked per client and content hash (`tool_guide_deliveries`).

## Writing your instructions

Good content: how you like work done, conventions, what to ask before touching, and your own workflows. For example: a script the project ships, how to run it with `cmd_shell`, and when to stop and wait for you.
