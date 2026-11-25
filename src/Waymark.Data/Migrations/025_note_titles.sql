-- Every note has a title: a few words that say what it is about, so a list of notes can show titles only and the reader picks which ones to open.
-- The writers (memory_save, the admin UI, the CLAUDE.md import, bundle import) require or derive one; the CHECK only caps the length here,
-- because ALTER TABLE tests a CHECK against the existing rows before the backfill below has run.
-- Backfill: the first sentence when it ends within 80 characters, else the body itself when it fits, else its first 79 characters and an ellipsis.
-- NoteRules.DeriveTitle applies the same rule in code (and also cuts at a word boundary). The owner can retitle any note afterwards.
ALTER TABLE notes ADD COLUMN title TEXT NOT NULL DEFAULT '' CHECK (length(title) <= 80);

UPDATE notes SET title = CASE
    WHEN instr(body, '. ') BETWEEN 2 AND 81 THEN substr(body, 1, instr(body, '. ') - 1)
    WHEN length(rtrim(body, '.')) = 0 THEN substr(body, 1, 80)
    WHEN length(body) <= 80 THEN rtrim(body, '.')
    ELSE rtrim(substr(body, 1, 79)) || '…'
END;

-- The full-text row indexes the title with the body (the body column, weight 4), so a word in the title finds the note.
UPDATE note_fts SET body = (SELECT n.title || ' ' || n.body FROM notes n WHERE n.id = note_fts.rowid)
WHERE rowid IN (SELECT id FROM notes);
