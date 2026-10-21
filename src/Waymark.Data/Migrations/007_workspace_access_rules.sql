-- Per-workspace file access rules the owner edits in the admin UI. They add to the built-in policy (blocks and one extra allowed file type); they never lift a built-in block.
CREATE TABLE workspace_access_rules (
    id           INTEGER PRIMARY KEY,
    workspace_id INTEGER NOT NULL REFERENCES workspaces (id) ON DELETE CASCADE,
    kind         TEXT    NOT NULL CHECK (kind IN ('BlockFolder', 'BlockFileName', 'BlockKeyword', 'AllowExtension')),
    pattern      TEXT    NOT NULL COLLATE NOCASE,
    note         TEXT    NOT NULL DEFAULT '',
    enabled      INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    created_utc  TEXT    NOT NULL
) STRICT;

CREATE UNIQUE INDEX ux_workspace_access_rules ON workspace_access_rules (workspace_id, kind, pattern);
