-- The ASP.NET Data Protection key ring on macOS. Each row is one key element as Data Protection wrote it, already encrypted with the
-- master key kept in the login Keychain, so the database file alone cannot decrypt the vault or the ngrok authtoken.
-- Windows keeps its DPAPI-protected key files next to the database and leaves this table empty.
CREATE TABLE data_protection_keys (
    id INTEGER PRIMARY KEY,
    friendly_name TEXT NOT NULL,
    xml TEXT NOT NULL,
    created_utc TEXT NOT NULL
) STRICT;
