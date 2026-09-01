-- Phase 1: the tenancy model and everything that guards it.
--
-- Naming: usernames, org slugs, team slugs and repo slugs are all stored already-normalised
-- (lowercase, trimmed) by the API, so plain text + UNIQUE is enough — no citext extension needed.
-- Roles/permissions/visibility are text + CHECK rather than PG enums so they can be evolved
-- without ALTER TYPE.

CREATE TABLE users (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    username      text NOT NULL UNIQUE,
    email         text NOT NULL UNIQUE,
    display_name  text NOT NULL DEFAULT '',
    password_hash text NOT NULL,
    is_site_admin boolean NOT NULL DEFAULT false,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE organisations (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    slug        text NOT NULL UNIQUE,
    name        text NOT NULL,
    description text NOT NULL DEFAULT '',
    created_by  bigint NOT NULL REFERENCES users(id),
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE org_members (
    org_id   bigint NOT NULL REFERENCES organisations(id) ON DELETE CASCADE,
    user_id  bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role     text NOT NULL DEFAULT 'member' CHECK (role IN ('owner', 'admin', 'member')),
    added_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (org_id, user_id)
);

CREATE TABLE teams (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    org_id      bigint NOT NULL REFERENCES organisations(id) ON DELETE CASCADE,
    slug        text NOT NULL,
    name        text NOT NULL,
    description text NOT NULL DEFAULT '',
    created_at  timestamptz NOT NULL DEFAULT now(),
    UNIQUE (org_id, slug)
);

CREATE TABLE team_members (
    team_id  bigint NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    user_id  bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    added_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (team_id, user_id)
);

CREATE TABLE repositories (
    id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    org_id         bigint NOT NULL REFERENCES organisations(id) ON DELETE CASCADE,
    slug           text NOT NULL,
    name           text NOT NULL,
    description    text NOT NULL DEFAULT '',
    visibility     text NOT NULL DEFAULT 'private' CHECK (visibility IN ('public', 'internal', 'private')),
    default_branch text NOT NULL DEFAULT 'main',
    is_archived    boolean NOT NULL DEFAULT false,
    created_by     bigint NOT NULL REFERENCES users(id),
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    UNIQUE (org_id, slug)
);

-- Ordered least -> most privileged. The API's PermissionResolver mirrors this order.
-- read < triage < write < maintain < admin
CREATE TABLE repo_collaborators (
    repo_id    bigint NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    user_id    bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    permission text NOT NULL DEFAULT 'read'
        CHECK (permission IN ('read', 'triage', 'write', 'maintain', 'admin')),
    PRIMARY KEY (repo_id, user_id)
);

CREATE TABLE repo_team_access (
    repo_id    bigint NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    team_id    bigint NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    permission text NOT NULL DEFAULT 'read'
        CHECK (permission IN ('read', 'triage', 'write', 'maintain', 'admin')),
    PRIMARY KEY (repo_id, team_id)
);

CREATE TABLE ssh_keys (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id      bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    title        text NOT NULL,
    key_type     text NOT NULL,
    public_key   text NOT NULL,
    fingerprint  text NOT NULL UNIQUE,       -- SHA256:base64, no padding
    created_at   timestamptz NOT NULL DEFAULT now(),
    last_used_at timestamptz
);

CREATE TABLE refresh_tokens (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id    bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash text NOT NULL UNIQUE,         -- sha256 hex of the opaque token
    issued_at  timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    user_agent text NOT NULL DEFAULT ''
);

-- Rename history so old URLs 301 to the current slug. scope is 'org' or 'repo:{org_id}'.
CREATE TABLE slug_redirects (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    scope      text NOT NULL,
    old_slug   text NOT NULL,
    new_slug   text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (scope, old_slug)
);

CREATE INDEX idx_org_members_user   ON org_members (user_id);
CREATE INDEX idx_team_members_user  ON team_members (user_id);
CREATE INDEX idx_teams_org          ON teams (org_id);
CREATE INDEX idx_repositories_org   ON repositories (org_id);
CREATE INDEX idx_repo_collab_user   ON repo_collaborators (user_id);
CREATE INDEX idx_repo_team_access_team ON repo_team_access (team_id);
CREATE INDEX idx_ssh_keys_user      ON ssh_keys (user_id);
CREATE INDEX idx_refresh_tokens_user ON refresh_tokens (user_id);

UPDATE instance_info SET schema_phase = 1;
