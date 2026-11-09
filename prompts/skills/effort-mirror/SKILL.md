---
name: effort-mirror
description: Brings senior-expert depth to substantial work by sizing the task in human effort, decomposing it into every unit task a senior would do over that time (research, alternatives, build, tests, fresh-eyes review, polish), then executing all of them in one run with evidence. Use for any non-trivial build, design, implementation, refactor, migration, analysis, or document — anything a skilled human would need more than an hour for — even when the user doesn't ask for depth. Skip quick questions and small edits.
---

# Effort Mirror

A senior human produces quality through many small jobs spread over days: reading, researching, comparing options, building, testing, sleeping on it, reviewing with fresh eyes, polishing. Time itself adds nothing; those jobs do. Identify them, then do every one in this run.

## 1. Size it

Estimate focused hours a senior expert would spend (not calendar time). Depth follows size:

| Estimate | Mode |
|---|---|
| < 1 h | Just do the task. Don't mention this skill. |
| 1 h – 1 day | Short unit-task list in your reasoning; brief report. |
| > 1 day | Full plan in a task file; checkpoints; full report. |

Size by the real scope, not the user's phrasing — "small fix" in a payment flow can be a day of work.

## 2. Decompose

List the unit tasks that senior would do, phase by phase. Typical phases (drop ones that don't apply, add domain-specific ones):

| Phase | Unit tasks |
|---|---|
| Understand | read existing material, map constraints, restate the real problem |
| Research | current docs, prior art, known failure reports |
| Design | ≥ 2 options, trade-offs, decision with reason |
| Build | smallest working slice first, then extend |
| Prove | run it, tests incl. error and edge paths, measure if performance matters |
| Fresh eyes | review as a skeptical senior who didn't write it; fix findings |
| Polish | naming, dead code, docs, consistency with surrounding work |
| Hand-off | what changed, why, risks, open items |

Each unit task needs a done criterion and evidence (output, test result, diff, source). A task without evidence isn't done.

## 3. Separate what time can't simulate

Some value a human gets over days comes from outside: stakeholder answers, production data, real users, review by colleagues. List these. Ask upfront when they block; otherwise state the assumption and continue. Never fabricate them.

## 4. Execute

- Do the unit tasks in dependency order.
- For > 1 day tasks, write the list to a task file (e.g. `PLAN.md`) and tick items off, so progress survives context loss or a resumed session.
- Fresh eyes: run the review as a separate pass after building — a subagent if available, otherwise re-read the work cold against the original request. One more pass if it found major issues.
- Report only what was actually done. Steps you couldn't run (no environment, no access) are listed as not done, not described as done.

## 5. Report

Lead with the result. Then, scaled to size: effort estimate, unit tasks done, tasks skipped and why, assumptions, remaining risks.
