-- Waymark schema v1: settings, OAuth clients, approvals, authorization codes, tokens, API keys, audit.
-- All tables are STRICT. Timestamps are fixed-width UTC text (yyyy-MM-ddTHH:mm:ss.fffZ), so text order is time order.
-- Credentials (codes, tokens, API keys, polling secrets) are stored only as SHA-256 hashes.

CREATE TABLE settings (
    scope_kind  TEXT    NOT NULL CHECK (scope_kind IN ('global', 'workspace', 'project', 'connection')),
    scope_id    TEXT    NOT NULL DEFAULT '',
    key         TEXT    NOT NULL,
    value_json  TEXT    NOT NULL CHECK (json_valid(value_json)),
    version     INTEGER NOT NULL DEFAULT 1,
    updated_utc TEXT    NOT NULL,
    PRIMARY KEY (scope_kind, scope_id, key),
    CHECK ((scope_kind = 'global') = (scope_id = ''))
) STRICT, WITHOUT ROWID;

CREATE TABLE settings_history (
    id          INTEGER PRIMARY KEY,
    scope_kind  TEXT NOT NULL,
    scope_id    TEXT NOT NULL,
    key         TEXT NOT NULL,
    old_json    TEXT,
    new_json    TEXT NOT NULL,
    changed_utc TEXT NOT NULL,
    changed_by  TEXT NOT NULL
) STRICT;

CREATE INDEX ix_settings_history_key ON settings_history (scope_kind, scope_id, key, id DESC);

CREATE TABLE oauth_clients (
    client_id          TEXT PRIMARY KEY,
    client_name        TEXT NOT NULL,
    redirect_uris_json TEXT NOT NULL CHECK (json_valid(redirect_uris_json)),
    registered_utc     TEXT NOT NULL,
    last_used_utc      TEXT,
    revoked_utc        TEXT
) STRICT, WITHOUT ROWID;

-- One row per authorization request. The polling secret is stored only as poll_hash.
-- The one-time authorization code is minted on the first poll after approval (code_issued_utc),
-- so its plaintext is never stored.
CREATE TABLE approvals (
    id                  INTEGER PRIMARY KEY,
    poll_hash           BLOB NOT NULL UNIQUE,
    pairing_code        TEXT NOT NULL,
    client_id           TEXT NOT NULL REFERENCES oauth_clients (client_id),
    client_name_claimed TEXT NOT NULL,
    redirect_uri        TEXT NOT NULL,
    redirect_host       TEXT NOT NULL,
    state               TEXT,
    code_challenge      TEXT NOT NULL,
    scope               TEXT NOT NULL,
    resource            TEXT NOT NULL,
    requested_utc       TEXT NOT NULL,
    expires_utc         TEXT NOT NULL,
    status              TEXT NOT NULL CHECK (status IN ('pending', 'approved', 'denied', 'expired')),
    decided_utc         TEXT,
    decided_by          TEXT,
    decision_reason     TEXT,
    code_issued_utc     TEXT
) STRICT;

CREATE INDEX ix_approvals_status ON approvals (status, expires_utc);
CREATE INDEX ix_approvals_client ON approvals (client_id, id DESC);

CREATE TABLE auth_codes (
    code_hash      BLOB PRIMARY KEY,
    approval_id    INTEGER NOT NULL UNIQUE REFERENCES approvals (id),
    client_id      TEXT NOT NULL REFERENCES oauth_clients (client_id),
    redirect_uri   TEXT NOT NULL,
    code_challenge TEXT NOT NULL,
    scope          TEXT NOT NULL,
    resource       TEXT NOT NULL,
    expires_utc    TEXT NOT NULL,
    consumed_utc   TEXT
) STRICT, WITHOUT ROWID;

CREATE INDEX ix_auth_codes_expiry ON auth_codes (expires_utc);

-- used_utc marks a refresh token that was already exchanged; presenting it again revokes the family.
CREATE TABLE tokens (
    token_hash  BLOB PRIMARY KEY,
    kind        TEXT NOT NULL CHECK (kind IN ('access', 'refresh')),
    family_id   TEXT NOT NULL,
    client_id   TEXT NOT NULL REFERENCES oauth_clients (client_id),
    approval_id INTEGER REFERENCES approvals (id),
    scope       TEXT NOT NULL,
    resource    TEXT NOT NULL,
    issued_utc  TEXT NOT NULL,
    expires_utc TEXT NOT NULL,
    used_utc    TEXT,
    revoked_utc TEXT
) STRICT, WITHOUT ROWID;

CREATE INDEX ix_tokens_family ON tokens (family_id);
CREATE INDEX ix_tokens_client ON tokens (client_id);
CREATE INDEX ix_tokens_approval ON tokens (approval_id);
CREATE INDEX ix_tokens_expiry ON tokens (expires_utc);

CREATE TABLE api_keys (
    id            INTEGER PRIMARY KEY,
    name          TEXT NOT NULL,
    key_hash      BLOB NOT NULL UNIQUE,
    key_prefix    TEXT NOT NULL,
    created_utc   TEXT NOT NULL,
    expires_utc   TEXT,
    last_used_utc TEXT,
    revoked_utc   TEXT
) STRICT;

CREATE TABLE audit_entries (
    id           INTEGER PRIMARY KEY,
    ts_utc       TEXT NOT NULL,
    category     TEXT NOT NULL CHECK (category IN ('auth', 'approval', 'config', 'tool')),
    actor        TEXT,
    action       TEXT NOT NULL,
    target       TEXT,
    args_redacted TEXT,
    duration_ms  INTEGER,
    result_bytes INTEGER,
    outcome      TEXT NOT NULL CHECK (outcome IN ('ok', 'denied', 'error')),
    error        TEXT
) STRICT;

CREATE INDEX ix_audit_ts ON audit_entries (ts_utc);
CREATE INDEX ix_audit_category ON audit_entries (category, id DESC);
