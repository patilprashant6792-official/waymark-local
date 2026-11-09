---
name: prompt-smith
description: The standard for writing any text an LLM reads as instructions — system prompts, skills (SKILL.md), CLAUDE.md / AGENTS.md, agent and subagent instructions, tool descriptions, custom instructions, prompt templates. Produces token-optimal prompts that keep every behavior, delivered as an editable Markdown file. Use whenever a prompt or skill is being created, edited, updated, reviewed, shortened, or debugged — including when another skill or workflow generates prompt text, or the user pastes one and says "improve this".
---

# Prompt Smith

A prompt is a spec read by a model. Target: maximum behavioral fidelity per token. Brevity is the means; a shorter prompt that loses one rule is a regression.

## Principles

1. **Expert rung.** Same meaning, fewer tokens: "Let's meet back at 8:30 am" → "Meet 08:30". Cut filler, hedges, restatement, politeness, narration, history.
2. **Count tokens, not words.** Common words ≈ 1 token; rare or ornate words split into several ("rendezvous" > "meet"). Use plain words, standard symbols (→ ≤ | /), established abbreviations (API, UTC). No invented shorthand: decode errors are silent.
3. **Machine reader, human maintainer.** Use tables, XML tags, key: value, arrows — models parse them instantly. Keep it scannable for the human who edits it later.
4. **Compress words, never meaning.** Keep each rule's reason (one clause), constraints, defaults + exceptions, output contract, edge cases, boundary-defining examples. Rules with reasons generalize; bare rules get applied literally or dropped.
5. **Calm, literal, positive.** Stacked CAPS / "CRITICAL" / "MUST" cause overtriggering on current models. Emphasis only for 1–2 true hard limits. State the target behavior, not only what to avoid.
6. **One fact, one place.** Duplicates drift and conflict. Where rules can collide, state precedence.
7. **Keep every behavior.** When editing, first list each instruction, constraint, example and format rule in the original; the rewrite covers all of them unless the user approves a removal. Add no behavior the user didn't ask for.
8. **Stand alone.** Nothing in the prompt may depend on the chat that produced it.

## Technique map

| Content | Form |
|---|---|
| Condition → action, mappings | table or `X → Y` lines |
| Distinct blocks, injected data | XML tags: `<context>`, `<rules>`, `<examples>`, `<input>` |
| Ordered process | numbered steps, one verb-first line each |
| Output format | literal template |
| Behavior hard to describe | 1–3 short varied examples, labeled illustrative |
| Many special cases | one default + exceptions |
| Bulky, sometimes-needed detail | separate file, loaded on demand |
| Long documents in context | documents first, task last |
| Cached or reused prompt | stable content first, variable last |
| Role | one line, only if it changes behavior |

## References

- Writing a skill, or a system prompt > ~200 lines → read `references/skills-and-system-prompts.md`.
- Compressing or reviewing an existing prompt → read `references/review.md`.

## Deliver

- Always a standalone Markdown file or artifact, never inline-only — the user iterates across turns. Kebab-case filename naming the purpose.
- Later edits update the same file at the same path; no forked versions unless asked.
- In chat, after the file, ≤ 5 lines: tokens before → after (%), what was merged or removed, assumptions, risks. Don't repeat the prompt in chat.
