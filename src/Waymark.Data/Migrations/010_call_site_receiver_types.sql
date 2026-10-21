-- A call site now remembers the declared type of its receiver when the file alone tells it, so code_callers can
-- leave out same-named methods of unrelated types. receiver_guessed marks a capitalised name taken for a static type.
-- Rows written before this migration have no receiver type; the index is only a copy of the files on disk, so it is
-- emptied here and the reconcile that runs at start-up fills it again (cascades remove types, members and search rows).

ALTER TABLE code_call_sites ADD COLUMN receiver_type TEXT;
ALTER TABLE code_call_sites ADD COLUMN receiver_guessed INTEGER NOT NULL DEFAULT 0;

DELETE FROM code_files;
