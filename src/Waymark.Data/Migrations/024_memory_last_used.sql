-- When a model last opened a flow or a note (memory_recall get, memory_recall notes for a flow, a file or a history, or an agent's edit or confirm), in UTC.
-- Display only for now. NULL means never. Kept out of updated_utc on purpose: that column orders the guide's flow index and the lists,
-- and the guide hash covers only note ids and content hashes, so a use never re-sends the workspace guide.
-- A note revision copies the value of the row it replaces (NoteStore.ReviseAsync).
ALTER TABLE feature_maps ADD COLUMN last_used_utc TEXT NULL;
ALTER TABLE notes ADD COLUMN last_used_utc TEXT NULL;
