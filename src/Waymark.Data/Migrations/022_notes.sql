-- Waymark Memory: short notes of the owner and of models, kept here instead of an instruction file in the repository.
-- A note belongs to a workspace and is anchored to the workspace as a whole (optionally 'about' a file or 'path:Type.Member'), to a flow, or to one step of a flow.
-- Provenance is on every row and comes from the server, never from tool arguments. An edit is a new row (replaces_id) and the old row is kept (superseded_utc);
-- a delete is a retirement (retired_utc). Deleting a flow detaches its notes (detached_from keeps the flow's name) instead of deleting them.
CREATE TABLE notes (
    id               INTEGER PRIMARY KEY,
    workspace_id     INTEGER NOT NULL REFERENCES workspaces (id) ON DELETE CASCADE,
    map_id           INTEGER NULL REFERENCES feature_maps (id) ON DELETE SET NULL,
    step_spec        TEXT    NULL COLLATE NOCASE,
    about            TEXT    NULL COLLATE NOCASE,
    detached_from    TEXT    NULL,
    kind             TEXT    NOT NULL DEFAULT 'note' CHECK (kind IN ('note', 'why', 'gotcha', 'convention', 'decision', 'howto')),
    body             TEXT    NOT NULL CHECK (length(body) BETWEEN 1 AND 1200),
    content_hash     TEXT    NOT NULL,
    author_kind      TEXT    NOT NULL CHECK (author_kind IN ('human', 'claude-client', 'internal-model')),
    author_id        TEXT    NOT NULL,
    code_hash        TEXT    NULL,
    flow_version     INTEGER NULL,
    confirmed_utc    TEXT    NULL,
    replaces_id      INTEGER NULL REFERENCES notes (id) ON DELETE SET NULL,
    superseded_utc   TEXT    NULL,
    retired_utc      TEXT    NULL,
    retired_by       TEXT    NULL,
    pinned           INTEGER NOT NULL DEFAULT 0 CHECK (pinned IN (0, 1)),
    created_utc      TEXT    NOT NULL,
    updated_utc      TEXT    NOT NULL,
    CHECK (step_spec IS NULL OR map_id IS NOT NULL OR detached_from IS NOT NULL),
    CHECK (about IS NULL OR map_id IS NULL)
) STRICT;

CREATE INDEX ix_notes_anchor ON notes (workspace_id, map_id, step_spec) WHERE retired_utc IS NULL AND superseded_utc IS NULL;
CREATE INDEX ix_notes_pinned ON notes (workspace_id) WHERE pinned = 1 AND retired_utc IS NULL AND superseded_utc IS NULL;
CREATE INDEX ix_notes_about ON notes (about) WHERE about IS NOT NULL;
CREATE INDEX ix_notes_replaces ON notes (replaces_id) WHERE replaces_id IS NOT NULL;
CREATE INDEX ix_notes_map ON notes (map_id) WHERE map_id IS NOT NULL;

-- BEFORE, so the flow's row is still there to read its name; ON DELETE SET NULL then clears map_id.
CREATE TRIGGER trg_feature_maps_detach_notes
BEFORE DELETE ON feature_maps
BEGIN
    UPDATE notes SET detached_from = OLD.name WHERE map_id = OLD.id;
END;

-- Full-text index of the active notes: rowid = notes.id. 'anchor' holds the flow name (dashes as spaces), the step's Type.Member, the about text and the kind.
-- The store writes and removes these rows in the same transaction as the note; this trigger only covers rows removed by a cascade.
CREATE VIRTUAL TABLE note_fts USING fts5 (body, anchor, tokenize = 'porter unicode61 remove_diacritics 2', prefix = '2 3');

CREATE TRIGGER trg_notes_fts_delete
AFTER DELETE ON notes
BEGIN
    DELETE FROM note_fts WHERE rowid = OLD.id;
END;
