-- The reason the AI gave for a command (the purpose argument), kept with the audit row so it can be read later.
ALTER TABLE audit_entries ADD COLUMN purpose TEXT;
