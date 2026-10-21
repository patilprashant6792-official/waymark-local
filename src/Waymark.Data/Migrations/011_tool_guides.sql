-- Usage guides that agents can ask for. A module's built-in guide ships inside its assembly; tool_guides holds only the owner's edits,
-- so deleting a row goes back to the built-in text. tool_guide_deliveries remembers which guide text each client last received
-- (by hash), so a changed guide can be announced to the clients that hold an older one.

CREATE TABLE tool_guides (
    module      TEXT NOT NULL PRIMARY KEY,
    content     TEXT NOT NULL,
    changed_utc TEXT NOT NULL,
    changed_by  TEXT NOT NULL
) STRICT;

CREATE TABLE tool_guide_deliveries (
    client_id     TEXT NOT NULL,
    module        TEXT NOT NULL,
    content_hash  TEXT NOT NULL,
    delivered_utc TEXT NOT NULL,
    PRIMARY KEY (client_id, module)
) STRICT;
