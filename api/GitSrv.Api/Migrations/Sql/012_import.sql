-- Phase 11.x: one-time import of an external repo by clone URL.

ALTER TABLE repositories ADD COLUMN import_source text;
ALTER TABLE repositories ADD COLUMN import_status text
    CHECK (import_status IN ('pending', 'importing', 'completed', 'failed'));
ALTER TABLE repositories ADD COLUMN import_error text NOT NULL DEFAULT '';

CREATE INDEX idx_repositories_import ON repositories (import_status) WHERE import_status IN ('pending', 'importing');

UPDATE instance_info SET schema_phase = 12;
