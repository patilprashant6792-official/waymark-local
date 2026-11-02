-- Feature maps: the code components Claude needed to understand a feature, saved under the feature's name (memorise / recall).
-- A component is a path or a name, never file content, so it stays valid across branches; recall checks each one against the code on disk.
-- Also removes the tables of the earlier flow-memory experiment, for a database that has them.
DROP TRIGGER IF EXISTS trg_flows_fts_delete;
DROP TABLE IF EXISTS flow_fts;
DROP TABLE IF EXISTS flow_trail_events;
DROP TABLE IF EXISTS flow_trails;
DROP TABLE IF EXISTS flow_steps;
DROP TABLE IF EXISTS flows;

CREATE TABLE feature_maps (
    id           INTEGER PRIMARY KEY,
    workspace_id INTEGER NOT NULL REFERENCES workspaces (id) ON DELETE CASCADE,
    name         TEXT    NOT NULL COLLATE NOCASE,
    summary      TEXT    NOT NULL DEFAULT '',
    created_utc  TEXT    NOT NULL,
    updated_utc  TEXT    NOT NULL,
    UNIQUE (workspace_id, name)
) STRICT;

CREATE TABLE feature_components (
    id      INTEGER PRIMARY KEY,
    map_id  INTEGER NOT NULL REFERENCES feature_maps (id) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    spec    TEXT    NOT NULL COLLATE NOCASE,
    note    TEXT    NOT NULL DEFAULT '',
    UNIQUE (map_id, spec),
    UNIQUE (map_id, ordinal)
) STRICT;

CREATE INDEX ix_feature_components_spec ON feature_components (spec);
