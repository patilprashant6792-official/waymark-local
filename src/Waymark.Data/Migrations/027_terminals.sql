-- Terminals: named slots (per workspace, or of the computer when workspace_id is NULL) that run one program at a time and keep it running until it is
-- stopped, like the tabs of an IDE's terminal panel. Every program started in a terminal is still a command run (approval, audit, history) and points
-- back here with terminal_id. The state, addresses and process of the program now running are kept on the terminal so the admin UI and the guides can
-- show them, and so the process can be found and ended after Waymark itself stopped without ending it.
-- launch_json keeps what is needed to start the last command again; approved_json the owner's approval of it (reused while nothing it depends on changed).
CREATE TABLE terminals (
    id               INTEGER PRIMARY KEY,
    workspace_id     INTEGER REFERENCES workspaces (id) ON DELETE CASCADE,
    workspace_name   TEXT    NOT NULL,
    name             TEXT    NOT NULL,
    folder           TEXT    NOT NULL,
    state            TEXT    NOT NULL CHECK (state IN ('idle', 'starting', 'ready', 'appdown', 'exited', 'crashed', 'stopped', 'waymarkstopped')),
    current_run_id   INTEGER,
    last_run_id      INTEGER,
    operation        TEXT,
    command_line     TEXT,
    urls_json        TEXT    NOT NULL DEFAULT '[]',
    ports_json       TEXT    NOT NULL DEFAULT '[]',
    pid              INTEGER,
    pid_start_utc    TEXT,
    program          TEXT,
    exit_code        INTEGER,
    state_note       TEXT,
    stopped_by       TEXT,
    ready_how        TEXT,
    started_utc      TEXT,
    ready_utc        TEXT,
    ended_utc        TEXT,
    last_output_utc  TEXT,
    approved_json    TEXT,
    launch_json      TEXT,
    created_utc      TEXT    NOT NULL,
    updated_utc      TEXT    NOT NULL
) STRICT;

-- SQLite treats NULLs as distinct in a UNIQUE constraint, so the system terminals get their own unique index.
CREATE UNIQUE INDEX ux_terminals_workspace_name ON terminals (workspace_id, name) WHERE workspace_id IS NOT NULL;
CREATE UNIQUE INDEX ux_terminals_system_name ON terminals (name) WHERE workspace_id IS NULL;
CREATE INDEX ix_terminals_state ON terminals (state);

ALTER TABLE command_runs ADD COLUMN terminal_id INTEGER REFERENCES terminals (id) ON DELETE SET NULL;
ALTER TABLE command_runs ADD COLUMN stopped_by TEXT;
CREATE INDEX ix_command_runs_terminal ON command_runs (terminal_id, id DESC);
