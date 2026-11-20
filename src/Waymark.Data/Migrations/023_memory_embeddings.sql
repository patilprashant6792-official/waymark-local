-- Search by meaning: one vector per note or flow and per model ('<provider>/<model>@<dimension>'), float32 little-endian, L2-normalised.
-- There is no queue table: an item is pending when the hash of its current text has no row for the active model, which survives a crash and a model switch.
-- Nothing is written here while the owner's switch is off.
CREATE TABLE memory_embeddings (
    workspace_id INTEGER NOT NULL REFERENCES workspaces (id) ON DELETE CASCADE,
    item_kind    TEXT    NOT NULL CHECK (item_kind IN ('note', 'flow')),
    item_id      INTEGER NOT NULL,
    model_id     TEXT    NOT NULL,
    dimension    INTEGER NOT NULL CHECK (dimension BETWEEN 64 AND 4096),
    text_hash    TEXT    NOT NULL,
    vector       BLOB    NOT NULL,
    created_utc  TEXT    NOT NULL,
    PRIMARY KEY (item_kind, item_id, model_id)
) STRICT;

CREATE INDEX ix_memory_embeddings_ws ON memory_embeddings (workspace_id, model_id);

CREATE TRIGGER trg_feature_maps_drop_embedding
AFTER DELETE ON feature_maps
BEGIN
    DELETE FROM memory_embeddings WHERE item_kind = 'flow' AND item_id = OLD.id;
END;
