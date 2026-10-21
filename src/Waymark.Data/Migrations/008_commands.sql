-- Commands Claude asked to run, the owner's per-operation gate choices, and the trusted build-file baseline of each workspace.
-- The output of a run lives in a file (output_path); only its size is stored here.
CREATE TABLE command_runs (
    id              INTEGER PRIMARY KEY,
    workspace_id    INTEGER REFERENCES workspaces (id) ON DELETE SET NULL,
    workspace_name  TEXT    NOT NULL,
    tier            TEXT    NOT NULL CHECK (tier IN ('recipe', 'shell')),
    operation       TEXT    NOT NULL,
    shell           TEXT,
    command_line    TEXT    NOT NULL,
    original_command TEXT,
    working_dir     TEXT    NOT NULL,
    purpose         TEXT    NOT NULL,
    status          TEXT    NOT NULL CHECK (status IN ('waitingapproval', 'queued', 'running', 'succeeded', 'failed', 'denied', 'timedout', 'cancelled')),
    exit_code       INTEGER,
    requested_utc   TEXT    NOT NULL,
    decided_utc     TEXT,
    started_utc     TEXT,
    ended_utc       TEXT,
    actor           TEXT    NOT NULL,
    decided_by      TEXT,
    ask_reason      TEXT,
    note            TEXT,
    risks           TEXT    NOT NULL DEFAULT '[]',
    output_bytes    INTEGER NOT NULL DEFAULT 0,
    output_path     TEXT
) STRICT;

CREATE INDEX ix_command_runs_workspace ON command_runs (workspace_id, id DESC);
CREATE INDEX ix_command_runs_status ON command_runs (status, id);
CREATE INDEX ix_command_runs_ended ON command_runs (ended_utc);

-- workspace_id 0 means every workspace; a row for one workspace wins over it.
CREATE TABLE command_gates (
    workspace_id INTEGER NOT NULL,
    operation    TEXT    NOT NULL,
    gate         TEXT    NOT NULL CHECK (gate IN ('off', 'ask', 'auto')),
    updated_utc  TEXT    NOT NULL,
    PRIMARY KEY (workspace_id, operation)
) STRICT, WITHOUT ROWID;

CREATE TABLE command_trust (
    workspace_id     INTEGER PRIMARY KEY REFERENCES workspaces (id) ON DELETE CASCADE,
    fingerprint_json TEXT NOT NULL,
    approved_utc     TEXT NOT NULL
) STRICT;
