-- The input and output of a tool call, kept (after secret hiding) for the call's details page. Kept apart from audit_entries so that the audit list stays
-- small and fast, the output is read only on demand, and the texts can be removed after a shorter period than the audit rows. A row here goes when its
-- audit row goes.
CREATE TABLE audit_payloads (
    audit_id     INTEGER PRIMARY KEY REFERENCES audit_entries(id) ON DELETE CASCADE,
    stored_utc   TEXT    NOT NULL,
    input_text   TEXT    NOT NULL,
    input_chars  INTEGER NOT NULL CHECK (input_chars >= 0),
    input_cut    INTEGER NOT NULL CHECK (input_cut IN (0, 1)),
    output_text  TEXT    NOT NULL,
    output_chars INTEGER NOT NULL CHECK (output_chars >= 0),
    output_cut   INTEGER NOT NULL CHECK (output_cut IN (0, 1))
) STRICT;

CREATE INDEX ix_audit_payloads_stored ON audit_payloads (stored_utc);
