-- Self-healing memory.
-- 1. Each flow step remembers the branch it was last seen in the code on. A step missing from the code is drift to repair only when it was last seen on the
--    branch checked out now (or the folder is not a git checkout); a step last seen on another branch is kept and left alone, and it is judged again when
--    that branch is checked out. NULL (steps saved before this) counts as another branch until the step is seen again.
-- 2. The repair queue: what drifted in a workspace's memory, found by the drift scanner after any change on disk, a branch switch or a memory save.
--    An open item is unique per (workspace, kind, target, step); it is resolved when a later check finds the target healthy, and an auto-fixed item keeps
--    what is needed to undo the fix. seq rises when an item is opened, so a client can be told about new items once.
ALTER TABLE feature_components ADD COLUMN branch TEXT NULL;

CREATE TABLE memory_repairs (
    id              INTEGER PRIMARY KEY,
    workspace_id    INTEGER NOT NULL REFERENCES workspaces (id) ON DELETE CASCADE,
    kind            TEXT    NOT NULL CHECK (kind IN ('link_broken', 'step_gone', 'step_moved', 'note_code_changed', 'note_flow_changed')),
    target_kind     TEXT    NOT NULL CHECK (target_kind IN ('flow', 'note')),
    target_id       INTEGER NOT NULL,
    target_name     TEXT    NOT NULL DEFAULT '',
    step_ordinal    INTEGER NOT NULL DEFAULT -1,
    step_spec       TEXT    NOT NULL DEFAULT '' COLLATE NOCASE,
    path            TEXT    NOT NULL DEFAULT '' COLLATE NOCASE,
    detail          TEXT    NOT NULL DEFAULT '',
    suggestion      TEXT    NULL,
    owner_only      INTEGER NOT NULL DEFAULT 0 CHECK (owner_only IN (0, 1)),
    state           TEXT    NOT NULL DEFAULT 'open' CHECK (state IN ('open', 'auto_fixed', 'resolved', 'undone')),
    seq             INTEGER NOT NULL,
    first_seen_utc  TEXT    NOT NULL,
    last_seen_utc   TEXT    NOT NULL,
    resolved_utc    TEXT    NULL,
    resolved_by     TEXT    NULL,
    undo_json       TEXT    NULL
) STRICT;

CREATE UNIQUE INDEX ux_memory_repairs_open ON memory_repairs (workspace_id, kind, target_kind, target_id, step_spec) WHERE state = 'open';
CREATE INDEX ix_memory_repairs_state ON memory_repairs (workspace_id, state, seq);
CREATE INDEX ix_memory_repairs_path ON memory_repairs (workspace_id, path) WHERE state = 'open';
CREATE INDEX ix_memory_repairs_target ON memory_repairs (workspace_id, target_kind, target_id) WHERE state = 'open';

-- Per workspace: the last seq handed out, and how many flow steps were left alone because they were last seen on another branch (from the last full scan).
CREATE TABLE memory_repair_state (
    workspace_id        INTEGER PRIMARY KEY REFERENCES workspaces (id) ON DELETE CASCADE,
    seq                 INTEGER NOT NULL DEFAULT 0,
    other_branch_steps  INTEGER NOT NULL DEFAULT 0,
    branch              TEXT    NULL,
    full_scan_utc       TEXT    NULL
) STRICT;
