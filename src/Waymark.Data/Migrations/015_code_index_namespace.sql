-- TypeScript has namespaces, and the code index now lists them as types of kind 'namespace'. A CHECK constraint of a STRICT table cannot be
-- altered, so the tables are created again (as in migration 013, the only change is the extra kind). The index is only a copy of the files
-- on disk, so nothing is lost: the reconcile that runs at start-up fills it again.

DROP TRIGGER IF EXISTS trg_code_types_fts_delete;
DROP TRIGGER IF EXISTS trg_code_members_fts_delete;
DROP TABLE IF EXISTS code_fts;
DROP TABLE IF EXISTS code_call_sites;
DROP TABLE IF EXISTS code_members;
DROP TABLE IF EXISTS code_types;
DROP TABLE IF EXISTS code_files;

CREATE TABLE code_files (
    id              INTEGER PRIMARY KEY,
    workspace_id    INTEGER NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
    rel_path        TEXT    NOT NULL COLLATE NOCASE,
    hash            TEXT    NOT NULL,
    size            INTEGER NOT NULL CHECK (size >= 0),
    mtime_utc       TEXT    NOT NULL,
    line_count      INTEGER NOT NULL CHECK (line_count >= 0),
    classification  TEXT    NOT NULL,
    language        TEXT    NOT NULL DEFAULT 'csharp' CHECK (language IN ('csharp', 'python', 'typescript', 'javascript')),
    usings          TEXT    NOT NULL,
    indexed_utc     TEXT    NOT NULL,
    UNIQUE (workspace_id, rel_path)
) STRICT;

CREATE TABLE code_types (
    id          INTEGER PRIMARY KEY,
    file_id     INTEGER NOT NULL REFERENCES code_files(id) ON DELETE CASCADE,
    ordinal     INTEGER NOT NULL CHECK (ordinal >= 0),
    kind        TEXT    NOT NULL CHECK (kind IN ('class', 'interface', 'struct', 'record', 'recordstruct', 'enum', 'delegate', 'typealias', 'namespace')),
    name        TEXT    NOT NULL,
    full_name   TEXT    NOT NULL,
    namespace   TEXT,
    base_list   TEXT,
    modifiers   TEXT,
    attributes  TEXT,
    doc_summary TEXT,
    start_line  INTEGER NOT NULL CHECK (start_line >= 1),
    end_line    INTEGER NOT NULL CHECK (end_line >= start_line),
    UNIQUE (file_id, ordinal)
) STRICT;

CREATE INDEX ix_code_types_file ON code_types (file_id);
CREATE INDEX ix_code_types_name ON code_types (name COLLATE NOCASE);

CREATE TABLE code_members (
    id          INTEGER PRIMARY KEY,
    file_id     INTEGER NOT NULL REFERENCES code_files(id) ON DELETE CASCADE,
    type_id     INTEGER REFERENCES code_types(id) ON DELETE CASCADE,
    kind        TEXT    NOT NULL CHECK (kind IN ('method', 'constructor', 'property', 'field', 'event', 'indexer', 'operator', 'enummember', 'function', 'variable')),
    name        TEXT    NOT NULL,
    signature   TEXT    NOT NULL,
    return_type TEXT,
    modifiers   TEXT,
    attributes  TEXT,
    doc_summary TEXT,
    start_line  INTEGER NOT NULL CHECK (start_line >= 1),
    end_line    INTEGER NOT NULL CHECK (end_line >= start_line)
) STRICT;

CREATE INDEX ix_code_members_file ON code_members (file_id);
CREATE INDEX ix_code_members_type ON code_members (type_id);
CREATE INDEX ix_code_members_name ON code_members (name COLLATE NOCASE);

CREATE TABLE code_call_sites (
    id               INTEGER PRIMARY KEY,
    file_id          INTEGER NOT NULL REFERENCES code_files(id) ON DELETE CASCADE,
    caller_type      TEXT,
    caller_member    TEXT,
    callee_name      TEXT    NOT NULL,
    receiver         TEXT,
    line             INTEGER NOT NULL CHECK (line >= 1),
    receiver_type    TEXT,
    receiver_guessed INTEGER NOT NULL DEFAULT 0
) STRICT;

CREATE INDEX ix_code_call_sites_file ON code_call_sites (file_id);
CREATE INDEX ix_code_call_sites_callee ON code_call_sites (callee_name COLLATE NOCASE);

CREATE VIRTUAL TABLE code_fts USING fts5 (
    name,
    full_name,
    doc,
    path,
    file_id UNINDEXED,
    kind UNINDEXED,
    tokenize = 'unicode61 remove_diacritics 2'
);

CREATE TRIGGER trg_code_types_fts_delete AFTER DELETE ON code_types
BEGIN
    DELETE FROM code_fts WHERE rowid = OLD.id * 2;
END;

CREATE TRIGGER trg_code_members_fts_delete AFTER DELETE ON code_members
BEGIN
    DELETE FROM code_fts WHERE rowid = OLD.id * 2 + 1;
END;
