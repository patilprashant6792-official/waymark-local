-- Feature maps become flows: one trigger to one outcome as ordered method steps (migration 017 stored an unordered set of components).
-- A step is still a name, never content. What changes: its depth below the trigger (0 is the trigger), how it is reached from the step above,
-- the folder-and-file part of its text (so "which flows use this file" is one indexed lookup), and per map a version that rises on every change.
-- Maps saved before this migration keep their components, in their order, at depth 0, and are marked unordered: an agent orders or splits them the next time it uses one.
ALTER TABLE feature_maps ADD COLUMN version INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1);
ALTER TABLE feature_maps ADD COLUMN ordered INTEGER NOT NULL DEFAULT 0 CHECK (ordered IN (0, 1));
ALTER TABLE feature_maps ADD COLUMN distinct_reason TEXT NOT NULL DEFAULT '';

ALTER TABLE feature_components ADD COLUMN depth INTEGER NOT NULL DEFAULT 0 CHECK (depth BETWEEN 0 AND 8);
ALTER TABLE feature_components ADD COLUMN link_kind TEXT NOT NULL DEFAULT 'call' CHECK (link_kind IN ('call', 'http', 'event', 'ui', 'di', 'job'));
ALTER TABLE feature_components ADD COLUMN path TEXT NOT NULL DEFAULT '' COLLATE NOCASE;

-- The text before ':' (a type or member) or before '#' (a line range); a whole file is its own path.
UPDATE feature_components
SET path = CASE
    WHEN instr(spec, ':') > 0 THEN substr(spec, 1, instr(spec, ':') - 1)
    WHEN instr(spec, '#') > 0 THEN substr(spec, 1, instr(spec, '#') - 1)
    ELSE spec
END;

CREATE INDEX ix_feature_components_path ON feature_components (path);
