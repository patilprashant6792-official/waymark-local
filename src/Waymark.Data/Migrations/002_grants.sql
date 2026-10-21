-- Recreates the tables that hold authorization codes, issued access/refresh pairs and API keys.
-- Nothing wrote to them before this migration, so dropping them loses no data.

DROP TABLE IF EXISTS auth_codes;
DROP TABLE IF EXISTS tokens;
DROP TABLE IF EXISTS api_keys;

CREATE TABLE auth_codes (
    code_hash      BLOB NOT NULL PRIMARY KEY,
    approval_id    INTEGER NOT NULL REFERENCES approvals (id),
    client_id      TEXT NOT NULL,
    redirect_uri   TEXT NOT NULL,
    code_challenge TEXT NOT NULL,
    scope          TEXT NOT NULL,
    resource       TEXT NOT NULL,
    expires_utc    TEXT NOT NULL,
    consumed_utc   TEXT
) STRICT;

CREATE INDEX ix_auth_codes_expires ON auth_codes (expires_utc);

CREATE TABLE tokens (
    token_hash  BLOB NOT NULL PRIMARY KEY,
    kind        TEXT NOT NULL CHECK (kind IN ('access', 'refresh')),
    family_id   TEXT NOT NULL,
    client_id   TEXT NOT NULL,
    approval_id INTEGER REFERENCES approvals (id),
    scope       TEXT NOT NULL,
    resource    TEXT NOT NULL,
    issued_utc  TEXT NOT NULL,
    expires_utc TEXT NOT NULL,
    used_utc    TEXT,
    revoked_utc TEXT
) STRICT;

CREATE INDEX ix_tokens_family ON tokens (family_id);
CREATE INDEX ix_tokens_client ON tokens (client_id);
CREATE INDEX ix_tokens_approval ON tokens (approval_id);
CREATE INDEX ix_tokens_expires ON tokens (expires_utc);

CREATE TABLE api_keys (
    id            TEXT NOT NULL PRIMARY KEY,
    name          TEXT NOT NULL,
    key_hash      BLOB NOT NULL UNIQUE,
    key_hint      TEXT NOT NULL,
    scope         TEXT NOT NULL,
    created_utc   TEXT NOT NULL,
    expires_utc   TEXT,
    last_used_utc TEXT,
    revoked_utc   TEXT
) STRICT;
