# file_replace: manual test

Run this in a **fresh** Claude session connected to Waymark, on a scratch branch of the waymark workspace (`cmd_git branch_create`), with `file_replace` switched on. The unit and integration tests run with `dotnet test`; this prompt checks the tool as an agent uses it.

## The prompt for the agent

```
You are testing Waymark's file_replace tool on the workspace named waymark, which is on a scratch branch. Use only Waymark tools. Do every step in order, and for each one write PASS or FAIL and the evidence (the call you made and what came back).

 1. Preview a literal replace: find 'ReplaceRefusedException', with 'ReplaceRejectedException', pathGlob 'src/**/*.cs', wholeWord true. Confirm the result says nothing was written, lists each file with marked lines, and gives a planId and a count.
 2. Apply it with a wrong confirmCount (the count plus one). Confirm it is refused and cmd_git status shows no change.
 3. Apply it with the right count. Confirm every file is listed with its count. Run cmd_project build: it must be clean.
 4. cmd_git diff: confirm only the renamed names changed and no line shows a line-ending-only change.
 5. Apply the same planId again. Confirm it is refused as unknown or used.
 6. Preview a C# rename: mode 'csharp', find 'ReplaceRejectedException', with 'ReplaceRefusedException', pathGlob '**'. Confirm non-C# files are counted as not C#. Exclude one line with excludeJson, and apply with the count lowered by that line's matches. Confirm the excluded line still has the old name (code_read).
 7. Preview again for what is left, edit one of the listed files with file_edit_lines (add a blank line at the end), then apply the plan. Confirm it is refused and names that file.
 8. Preview a replace whose 'with' is 'AKIAABCDEFGHIJKLMNOP'. Confirm it is refused as a secret.
 9. Preview with pathsJson naming a file under bin/. Confirm there is no planId and the path is listed as refused.
10. Restore everything with cmd_git restore and confirm cmd_git status is clean.
```

## What you check yourself

- The audit log has one row per call; the apply rows show `find`, `with` and `planId`.
- The Console showed no build or test errors.
