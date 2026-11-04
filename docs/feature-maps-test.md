# Feature maps (flows): manual test

Run this in a **fresh** Claude session connected to Waymark, with the branch you want to test checked out. There is no test project yet: the agent runs the prompt and reports, and you check the page and the audit log yourself (part 2).

## Part 1: the prompt for the agent

```
You are testing Waymark's feature maps, which are flows: one trigger to one outcome as ordered method steps. The workspace is named waymark. Use only Waymark tools. Do every step in order, and for each one write PASS or FAIL and the evidence (the call you made and what came back). Count your Waymark calls per step. Do not skip a step because it looks like it will pass.

A. Adoption (do not call recall or memorise until step A3)
 1. Call waymark_guide. Quote the line under "Feature maps (flows) saved" exactly, and one flow line.
 2. Without any search or read tool, say which saved flow would help with: "how does Waymark decide whether a command runs, asks or is refused".
 3. Get that flow with recall(action='get', ids=...). Say how many steps came back, how many were not on this branch, whether it is marked unordered, and how long the answer was. Confirm it contains no code.

B. Trace and save (a feature that has no flow yet)
 4. Trace "how the access rules decide whether a file may be read" with the other Waymark tools until you could safely change it. Count the calls.
 5. Save it with memorise(action='save_feature_map', name, summary, steps). The flow has one trigger, at least three levels, at least one step under a step that is not the first, a note on two steps, and at least one link that a call cannot show, marked with ~di, ~event, ~http, ~ui or ~job. Quote the reply.
 6. recall get it. Confirm the order and indent are what you wrote, every link is marked, and the version is v1. Then change it: save the whole flow again with expectedVersion=1, one step added and one note changed. Confirm the reply says v2 and that nothing else was lost.
 7. recall(action='list', query=<the name of one method of the flow>). Confirm the flow is found by a method name. Then recall get it again and confirm that a step saved as a line range, if you used one, is marked as possibly shifted.

C. Refusals (each call must be refused with a clear message; say what the message was)
 8. A step whose member does not exist in a real file.
 9. A step whose file does not exist.
 10. A name with capitals and spaces that is not valid; then a valid name without a summary.
 11. An absolute path, and a path with '..'.
 12. Steps with two lines at depth 0; a step indented two levels below the line above; an indent with a tab; the same step twice.
 13. A flow where a step is indented under a method that does not call it (both methods exist). The message must say which step and that the step above makes no such call.
 14. A new flow whose first step is the first step of an existing flow, without distinct: refused, naming the other flow. Then again with distinct='...': saved. Delete the second flow afterwards.
 15. A new flow with the name of an existing one and no expectedVersion: refused, naming the version to pass.
 16. A change with expectedVersion that is not current: refused, and the reply contains the current flow.
 17. recall get with ids='abc', ids='999' and an unknown action; memorise with newName but no expectedVersion.
 18. A summary that contains a made-up credential, for example a connection string with a password in it: refused, and nothing is saved.

D. Another branch (simulate it)
 19. Create docs/scratch-a.md (3 lines) and src/Waymark.Modules.Code/Memory/ScratchProbe.cs with a class ScratchProbe that has methods Alpha and Gamma, where Alpha calls Gamma. Save a flow 'scratch-probe': ScratchProbe.Alpha as the trigger, ScratchProbe.Gamma under it, and docs/scratch-a.md#L1-2 under Gamma with ~ui.
 20. Rename Alpha to Beta (and its call site). recall get the flow: Alpha must be marked NOT ON THIS BRANCH with the names the file has, Gamma must still be shown, and the link of Gamma must say it is not judged, not that it does not call.
 21. Delete both scratch files. recall get again: every step is not on this branch, none is dropped from the flow (recall list still shows 3 steps).
 22. Recreate the files as they were at step 19. recall get: all steps are back and the link of Gamma says it calls it.
 23. Rename Alpha to Beta again, then update the flow with expectedVersion: add one step that does exist (any real method under Gamma). The save must succeed while Alpha, which is not on this branch, stays in the flow. Rename Beta back to Alpha.
 24. Save a second flow 'scratch-overlap' with a different first step (a real method of your choice) that has at least half of its steps in common with the first. The reply must name the first flow. Delete both flows with delete_feature_map and delete the scratch files. Confirm recall list no longer shows them.

E. Reading less
 25. recall get a flow of at least five steps. Then read only the two steps that matter for a change you describe, with code_method. Say how many characters recall returned and how many code_method returned, and why you did not read the rest.

F. The hint after a change (use the scratch files of step 19 again)
 26. With the flow saved and the files in place, edit Alpha with file_edit_lines so that it no longer calls Gamma. The result must end with a line starting with 🧭 that names the flow and the link that no longer holds. Restore the call.
 27. Edit the body of Gamma without changing what calls it, and edit a file that is in no flow. Neither result may contain a 🧭 line.
 28. file_move the scratch file to another name: the result must say that steps of the flow are no longer there. Move it back.
 29. file_delete the scratch file: the same. Recreate it, then delete the flow and the scratch files.

G. A map from before flows
 30. recall list and pick a map that is marked unordered. recall get it: it must say it is an unordered list. Put its steps in call order under one trigger (split it if it has more than one) and save it with expectedVersion. Confirm that the version rose, that the page shows a tree and that no step you left unchanged was refused for being on another branch.

End with a table: step, PASS/FAIL, calls, and anything that surprised you. Do not fix anything.
```

## Part 2: what you check yourself

| Where | Check |
|---|---|
| Workspace page, section **Feature maps** | The flows from the run are listed. A flow saved by the agent appears without reloading. Steps are indented under the step that calls them, with the version (v1, v2) and an **unordered** badge on a map from before flows. |
| Same page | Each step shows **Here** or **Not on this branch**, and the next column shows what the code says about the link from the step above: **Calls it**, **By name only**, **Declared**, **Does not call**, **Not judged**. Hovering a chip explains it. |
| Same page | **Check against the code** marks a step whose file you rename as "Not on this branch" (with "moved?" when a method of that name is in another file) and "Here" again when you rename it back. A link whose step is not here shows "Not judged", never "Does not call". A call you delete from a method shows "Does not call" and a red chip "1 link does not call" in the map header. |
| Same page | **Remove** is disabled on the trigger and on a step that has steps under it, with the reason on hover. On another step it asks first, saves a new version, and shows in the audit log as `config.feature_component_removed`; **Delete map** asks first and shows as `config.feature_map_deleted`. **Copy steps** puts the indented text on the clipboard and memorise accepts it as it is. |
| Host prompt page | The preview shows "Feature maps (flows) saved" with one line per flow: id, name, steps, a short summary, and ", unordered" where it applies. |
| Audit page | `recall` and `memorise` calls are listed with their tokens. A `recall get` is much smaller than before (no code), and a session that traced a feature with `code_*` calls and never called `recall` is the adoption miss to look for. |
| Export and import | **Export feature maps** writes a file with `"formatVersion": 2`, with `depth` and `link` on each step. Importing it into a workspace that has a flow of the same name and the same steps says "here already"; with other steps it says "not added" and leaves yours as it is. A version 1 file (an export from before this change) imports as unordered maps. |
| Log | With the log at Debug, a save, a delete and an import write nothing about the semantic index (it is not built); no warning about it appears. |
| Switch branch | Steps saved on the other branch show "Not on this branch" and are still in the list; updating the flow on this branch keeps them. |
| Setting | `Waymark:FeatureMaps:EditHints=false` removes the 🧭 line from file tool results. |

## Judging it

The feature earns its place if, over about two weeks of real use, `recall` is called before exploring in most sessions that touch a saved feature, those sessions need clearly fewer `code_*` calls than the first exploration did, and flows are changed (updated, not duplicated) when a path changes and left alone when only code inside a step changed. Measured on this repository before flows: command-gates 11 calls and about 11.6k tokens, tool-call-audit 10 and 11.0k, python-indexing 7 and 8.0k; with maps as component lists, one `get` of about 2 to 3k tokens each. With flows the target is one `get` of well under 1k tokens, plus one to three `code_method` reads of the steps the task needs. Also count duplicates: two flows with the same trigger and outcome that nobody deleted. If agents do not call `recall` first, if the flows go stale faster than they are fixed, or if the 🧭 line is noise more often than help, drop it or switch the hint off.
