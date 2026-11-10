-- code_search now searches more than names: the full-text table gets the split words of each name ('tokens'), and the words of a member's body
-- ('terms'), and is read with stemming (porter) and 2-3 letter prefix indexes. A virtual table cannot be altered, so it is created again.
-- The index is only a copy of the files on disk, so nothing is lost: code_files is emptied and the reconcile that runs at start-up fills
-- everything again, which also writes the new columns.

DROP TRIGGER IF EXISTS trg_code_types_fts_delete;
DROP TRIGGER IF EXISTS trg_code_members_fts_delete;
DROP TABLE IF EXISTS code_fts;

CREATE VIRTUAL TABLE code_fts USING fts5 (
    name,
    full_name,
    tokens,
    doc,
    path,
    terms,
    file_id UNINDEXED,
    kind UNINDEXED,
    tokenize = 'porter unicode61 remove_diacritics 2',
    prefix = '2 3'
);

CREATE TRIGGER trg_code_types_fts_delete AFTER DELETE ON code_types
BEGIN
    DELETE FROM code_fts WHERE rowid = OLD.id * 2;
END;

CREATE TRIGGER trg_code_members_fts_delete AFTER DELETE ON code_members
BEGIN
    DELETE FROM code_fts WHERE rowid = OLD.id * 2 + 1;
END;

DELETE FROM code_files;
