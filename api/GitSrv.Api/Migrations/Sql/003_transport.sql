-- Phase 2: Git transport (HTTP + SSH).

-- Personal access tokens — used as the password in HTTP Basic auth for git, and (later) for the API.
-- Only the SHA-256 hash is stored; the token is shown once at creation.
CREATE TABLE personal_access_tokens (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id      bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    name         text NOT NULL,
    token_hash   text NOT NULL UNIQUE,        -- sha256 hex of the full token
    token_prefix text NOT NULL,               -- e.g. 'gsp_1a2b3c4d' for identification in the UI
    scope_read   boolean NOT NULL DEFAULT true,
    scope_write  boolean NOT NULL DEFAULT true,
    created_at   timestamptz NOT NULL DEFAULT now(),
    last_used_at timestamptz,
    expires_at   timestamptz
);
CREATE INDEX idx_pat_user ON personal_access_tokens (user_id);

-- Repo size, refreshed after each successful push. Feeds quota checks and the org storage view.
ALTER TABLE repositories ADD COLUMN size_bytes bigint NOT NULL DEFAULT 0;
ALTER TABLE repositories ADD COLUMN pushed_at timestamptz;

-- Bump last_used_at on ssh_keys is already possible (column exists from 002).

UPDATE instance_info SET schema_phase = 2;
