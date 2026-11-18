# Guides and the host prompt

Waymark teaches a connected agent in three layers, all delivered through MCP tool results, so they work with or without a skill.

## The overview (host prompt)

A short text, written by Waymark, that an agent loads once per conversation with `waymark_guide`. The server instructions tell it to, and the tool is marked `anthropic/alwaysLoad` for clients that support it. It holds:

- the four groups (code, packages, files, commands) and how to ask for a group's guide;
- a few cross-cutting rules: a refusal is a decision, a missing tool is switched off, never write a `[redacted:...]` marker into a file, what to do if a secret shows up, ask before anything hard to undo;
- **your instructions**, under "Instructions from the owner" (edit them on the **Host prompt** page; up to 40,000 characters; secrets are refused because every agent receives the text);
- the **live setup**: tools switched off, one line per workspace (on or off, your summary of it, how many flows it has, whether it has instructions), and the command operations that ask first or are refused.

The template is `src/Waymark.Host/Guide.md` (markers `{{owner}}` and `{{setup}}`). Your text is stored in the `tool_guides` table under the key `host`. The hash covers the template and your text, not the live setup, so a settings change does not re-announce the overview.

## Workspace guides

Each workspace has its own guide, which an agent loads with `waymark_guide(workspace='<name>')`. It holds:

- your **summary** of the workspace (one line, up to 200 characters; it is also listed in the overview so an agent can pick the right workspace);
- your **instructions** for that workspace, under "Instructions from the owner for this workspace" (edit both on the workspace's **Instructions** tab; up to 20,000 characters; secrets are refused). For work in that workspace they win over the host prompt; gates and approvals still apply;
- its **live setup**: the newest 15 flows and the command gates set for that workspace alone.

The text is stored in `workspace_guides` (one row per workspace, removed with the workspace). Delivery is tracked under the module key `ws:<workspace id>`. The hash covers the name, summary and instructions, not the flows, so saving a flow does not re-announce the guide.

An agent is reminded in two ways, because Waymark tracks deliveries per access key and cannot tell conversations apart:

- a call in a workspace that has instructions the client never loaded, or holds an older version of, carries a one-line notice (after the overview's notice, before a group's);
- the first group guide of a conversation (`guide: true`) in a workspace with instructions carries a one-line reminder to load them.

## Group guides

One built-in guide per group (`Guide.md` in each module, embedded in the assembly). They cover the tools, the order of work, long runs and hand-offs, project scripts and restarting Waymark itself. They are not editable; they change with Waymark.

An agent gets one by adding `guide: true` to its first call to that group. The first group guide also brings the overview if the agent has not loaded it.

## Notices

An agent that never asked is told once, in a tool result, that a guide exists. After you save new instructions, every agent that holds the old overview is told once on its next call. Delivery is tracked per client and content hash (`tool_guide_deliveries`).

## Writing your instructions

Good content: how you like work done, conventions, what to ask before touching, and your own workflows. For example: a script the project ships, how to run it with `cmd_shell`, and when to stop and wait for you.

A ready-made set to start from (a language-neutral engineering prompt and two skills) is in [prompts/](../prompts/README.md). Personal Claude preferences are read before this page, so that is the place for any line that must apply before Claude decides to use Waymark.
