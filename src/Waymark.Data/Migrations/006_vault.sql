-- The vault keeps named secrets. Values are stored encrypted (ASP.NET Data Protection); the hint is the last characters, for recognising a secret.
CREATE TABLE vault_secrets (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL COLLATE NOCASE,
    protected_value TEXT NOT NULL,
    hint TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    last_used_utc TEXT NULL
) STRICT;

CREATE UNIQUE INDEX ux_vault_secrets_name ON vault_secrets (name);
