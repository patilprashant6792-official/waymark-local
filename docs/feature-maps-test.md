# Feature maps: manual test

Run this in a **fresh** Claude session connected to Waymark, with the branch you want to test checked out. There is no test project: the agent runs the prompt and reports, and you check the page and the audit log yourself (part 2).

## Part 1: the prompt for the agent

```
You are testing Waymark's feature maps in the workspace named waymark. Use only Waymark tools. Do every step in order, and for each one write PASS or FAIL and the evidence (the call you made and what came back). Count your Waymark calls per step. Do not skip a step because it looks like it will pass.

A. Adoption (do not call recall or memorise until step A3)
 1. Call waymark_guide. Quote the line under "Feature maps saved" exactly.
 2. Without any search or read tool, say which saved map would help with: "how does Waymark decide whether a command runs, asks or is refused".
 3. Get that map with recall(action='get', ids=...). Say how many components came back, how many were not available, and the tokens line.

B. Explore and save (a feature that has no map yet)
 4. Explore "how the access rules decide whether a file may be read" with the other Waymark tools until you could safely change it. Count the calls.
 5. Save it with memorise(action='save_feature_map'). Use every component form at least once: a file ('path'), a type ('path:Type'), a member ('path:Type.Member') and a line range ('path#L10-40'), and give two of them a note after ' | '.
 6. Save again under the same name with one new component and one note changed. Confirm it merged: nothing was dropped.
 7. Call recall(action='list', query=<one word from the summary>), then recall(action='get', ids=<the new id>). Confirm every component comes back and the line range is marked as possibly shifted.

C. Refusals (each call must be refused with a clear message; say what the message was)
 8. A member that does not exist in a real file.
 9. A file that does not exist.
 10. A name with capitals and spaces that is not valid, then the same with a valid name but no components.
 11. An absolute path, and a path with '..'.
 12. recall get with ids='abc', ids='999', and an unknown action.
 13. A summary that contains a made-up secret such as password=hunter2hunter2.

D. Another branch (simulate it)
 14. Create docs/scratch-a.md (3 lines) and src/Waymark.Modules.Code/Memory/ScratchProbe.cs with a class ScratchProbe that has methods Alpha and Gamma. Save a map 'scratch-probe' with ScratchProbe.Alpha, ScratchProbe.Gamma and docs/scratch-a.md#L1-2.
 15. Rename Alpha to Beta. recall get the map: Alpha must be listed as not available with the names the file has, and the other two must still be shown.
 16. Delete both scratch files. recall get again: all three are not available, none is dropped from the map (recall list still shows 3 components).
 17. Recreate the files as they were. recall get: all three are back.
 18. Save a second map 'scratch-overlap' that shares at least half of its components with the first. The reply must name the first map. Delete both maps with delete_feature_map and delete the scratch files. Confirm recall list no longer shows them.

E. Budget
 19. recall get with ids of three saved maps and maxTokens=500. Confirm nothing is cut: what does not fit is listed with a call to read it.

End with a table: step, PASS/FAIL, calls, and anything that surprised you. Do not fix anything.
```

## Part 2: what you check yourself

| Where | Check |
|---|---|
| Workspace page, section **Feature maps** | The maps from the run are listed. A map saved by the agent appears without reloading. |
| Same page | **Check against the code** marks a component whose file you rename as "Not on this branch", and "Here" again when you rename it back. |
| Same page | **Remove** on one component, then **Delete map**: each asks first, and each shows in the audit log as `config.feature_component_removed` and `config.feature_map_deleted`. |
| Host prompt page | The preview shows "Feature maps saved" with names and ids, and the bullets describe the maps. |
| Audit page | `recall` and `memorise` calls are listed with their tokens. A session that explored a feature with `code_*` calls and never called `recall` is the adoption miss to look for. |
| Switch branch | Components saved on the other branch show "Not on this branch" and are still in the list. |

## Judging it

The feature earns its place if, over about two weeks of real use, `recall` is called before exploring in most sessions that touch a saved feature, and those sessions need clearly fewer `code_*` calls than the first exploration did. Measured on this repository before the feature: command-gates 11 calls and about 11.6k tokens, tool-call-audit 10 and 11.0k, python-indexing 7 and 8.0k; with the maps, one `get` of about 2 to 3k tokens each. If agents do not call `recall` first, or the maps go stale faster than they are fixed, drop it.
