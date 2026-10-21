CREATE TABLE workspaces (
    id               INTEGER PRIMARY KEY,
    name             TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    path             TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    enabled          INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    added_utc        TEXT    NOT NULL,
    last_indexed_utc TEXT
) STRICT;

CREATE TABLE tool_settings (
    tool_name   TEXT    NOT NULL PRIMARY KEY,
    enabled     INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    changed_utc TEXT    NOT NULL,
    changed_by  TEXT    NOT NULL
) STRICT;
