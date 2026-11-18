-- The owner's own text for one workspace: a short summary that the overview lists, and instructions that only the workspace's guide carries.
-- One row per workspace; removing the workspace removes its row. The guide an agent receives is tracked in tool_guide_deliveries under the
-- module key 'ws:<workspace id>'. Workspace ids can be given again after a removal (INTEGER PRIMARY KEY without AUTOINCREMENT), so the trigger
-- forgets those deliveries too: a new workspace with an old id must not look as if a client already held its guide.

CREATE TABLE workspace_guides (
    workspace_id INTEGER PRIMARY KEY REFERENCES workspaces (id) ON DELETE CASCADE,
    summary      TEXT    NOT NULL DEFAULT '',
    instructions TEXT    NOT NULL DEFAULT '',
    changed_utc  TEXT    NOT NULL,
    changed_by   TEXT    NOT NULL
) STRICT;

CREATE TRIGGER trg_workspaces_delete_guide_deliveries
AFTER DELETE ON workspaces
BEGIN
    DELETE FROM tool_guide_deliveries WHERE module = 'ws:' || OLD.id;
END;
