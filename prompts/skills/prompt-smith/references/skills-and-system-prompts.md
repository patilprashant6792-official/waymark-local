# Skills and large system prompts

Token cost depends on load tier, not just length. Optimize per tier; moving bulk down a tier usually saves more than word-level trimming.

| Tier | Loaded | Rules |
|---|---|---|
| Description (frontmatter) | always | It is the trigger. State what + when; ~100 words max. Claim ownership ("The standard for…"), list the nouns and verbs users actually say, name indirect cases (e.g. "when another skill generates prompt text"). Slightly pushy — models tend to undertrigger skills. |
| Body | on trigger | Only what nearly every run needs. Well under 500 lines. |
| `references/`, `scripts/` | on demand | Detail needed only in some runs. Point to each with a condition: "Read `references/x.md` when Y." A file read on every run belongs in the body — splitting it only adds a tool call. |

## Hand-offs between skills

A description won't reliably trigger mid-workflow while another skill is active. In the calling skill, at the step that needs it, add one calm line naming the skill (not a path — install paths vary):

> When writing or editing any prompt text, read and follow the `prompt-smith` skill first.

## Large system prompts

- Group by concern under short headers; no rule outside its section.
- State precedence once where sections can conflict.
- Move rarely-hit procedures (edge-case playbooks, long examples) to tool descriptions or retrievable files when the runtime supports it.
