-- Phase 0: walking-skeleton schema. The real tenancy model (users, organisations, teams,
-- repositories) arrives in Phase 1 as 002_identity.sql. This migration exists to prove the
-- runner works end to end and to give /health something real to read.

CREATE TABLE instance_info (
    id            integer PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    schema_phase  integer NOT NULL,
    initialised_at timestamptz NOT NULL DEFAULT now()
);

INSERT INTO instance_info (id, schema_phase) VALUES (1, 0);
