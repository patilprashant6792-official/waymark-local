-- NuGet symbol index. One row per (package, version, target framework) in nuget_packages; its symbols are replaced as a whole.
-- A package is searchable only while state = 'ready'. 'building' doubles as the build lock (started_utc tells a stale one).
CREATE TABLE nuget_packages (
    id             INTEGER PRIMARY KEY,
    package_id     TEXT    NOT NULL COLLATE NOCASE,
    version        TEXT    NOT NULL COLLATE NOCASE,
    tfm            TEXT    NOT NULL COLLATE NOCASE,
    state          TEXT    NOT NULL CHECK (state IN ('building', 'ready', 'failed')),
    schema_version INTEGER NOT NULL,
    symbol_count   INTEGER NOT NULL DEFAULT 0,
    type_count     INTEGER NOT NULL DEFAULT 0,
    started_utc    TEXT    NOT NULL,
    indexed_utc    TEXT,
    error          TEXT,
    UNIQUE (package_id, version, tfm)
) STRICT;

-- Types and method groups only; properties, fields and events live inside their declaring type's pre-rendered sections.
-- Rows are never updated (a rebuild deletes the package row and inserts again), which keeps nuget_fts exact.
CREATE TABLE nuget_symbols (
    id             INTEGER PRIMARY KEY,
    package_row_id INTEGER NOT NULL REFERENCES nuget_packages (id) ON DELETE CASCADE,
    sid            TEXT    NOT NULL,
    kind           TEXT    NOT NULL CHECK (kind IN ('class', 'interface', 'enum', 'struct', 'delegate', 'methodgroup')),
    name           TEXT    NOT NULL COLLATE NOCASE,
    full_name      TEXT    NOT NULL COLLATE NOCASE,
    namespace      TEXT    NOT NULL COLLATE NOCASE,
    declaring_type TEXT    COLLATE NOCASE,
    declaration    TEXT    NOT NULL,
    card           TEXT    NOT NULL,
    prominence     REAL    NOT NULL,
    tokens         TEXT    NOT NULL,
    summary        TEXT    NOT NULL,
    related        TEXT    NOT NULL,
    d_sig          TEXT    NOT NULL,
    d_methods      TEXT    NOT NULL,
    d_props        TEXT    NOT NULL,
    d_data         TEXT    NOT NULL,
    d_doc          TEXT    NOT NULL,
    UNIQUE (package_row_id, sid)
) STRICT;

CREATE INDEX ix_nuget_symbols_full_name ON nuget_symbols (package_row_id, full_name);
CREATE INDEX ix_nuget_symbols_name ON nuget_symbols (package_row_id, name);
CREATE INDEX ix_nuget_symbols_prominence ON nuget_symbols (package_row_id, prominence DESC);

-- Namespace map behind nuget_outline (type count per namespace, display form).
CREATE TABLE nuget_namespaces (
    package_row_id INTEGER NOT NULL REFERENCES nuget_packages (id) ON DELETE CASCADE,
    namespace      TEXT    NOT NULL,
    type_count     INTEGER NOT NULL,
    PRIMARY KEY (package_row_id, namespace)
) STRICT;

-- External-content FTS5 index over nuget_symbols (a single backing table, so no copy of the text is stored).
-- Identifiers are split by unicode61 at dots and underscores; camel-case words are pre-split into the tokens column.
CREATE VIRTUAL TABLE nuget_fts USING fts5 (
    name, full_name, namespace, summary, tokens,
    content = 'nuget_symbols', content_rowid = 'id', tokenize = 'unicode61'
);

CREATE TRIGGER nuget_symbols_ai AFTER INSERT ON nuget_symbols BEGIN
    INSERT INTO nuget_fts (rowid, name, full_name, namespace, summary, tokens)
    VALUES (new.id, new.name, new.full_name, new.namespace, new.summary, new.tokens);
END;

-- Fires for rows removed by the ON DELETE CASCADE of a dropped or replaced package too.
CREATE TRIGGER nuget_symbols_ad AFTER DELETE ON nuget_symbols BEGIN
    INSERT INTO nuget_fts (nuget_fts, rowid, name, full_name, namespace, summary, tokens)
    VALUES ('delete', old.id, old.name, old.full_name, old.namespace, old.summary, old.tokens);
END;

-- Reflection metadata of a loaded package as JSON, so an unchanged package is not loaded again.
-- cache_key is chosen by the loader; resolved_version is the concrete version the payload was read from.
CREATE TABLE nuget_metadata_cache (
    cache_key        TEXT NOT NULL PRIMARY KEY,
    resolved_version TEXT NOT NULL,
    payload          TEXT NOT NULL,
    stored_utc       TEXT NOT NULL
) STRICT;
