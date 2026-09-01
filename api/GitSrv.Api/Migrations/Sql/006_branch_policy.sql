-- Phase 6: branch protection, releases, repo webhooks.

CREATE TABLE branch_protections (
    id                     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repo_id                bigint NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    pattern                text NOT NULL,                 -- exact name or glob ('release/*')
    require_pull_request   boolean NOT NULL DEFAULT true,
    required_approvals     integer NOT NULL DEFAULT 0,
    require_status_checks  boolean NOT NULL DEFAULT false,
    block_force_push       boolean NOT NULL DEFAULT true,
    block_deletion         boolean NOT NULL DEFAULT true,
    require_linear_history boolean NOT NULL DEFAULT false,
    restrict_push          boolean NOT NULL DEFAULT false, -- only maintainers may push directly
    created_at             timestamptz NOT NULL DEFAULT now(),
    UNIQUE (repo_id, pattern)
);

CREATE TABLE releases (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repo_id       bigint NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    tag_name      text NOT NULL,
    target_sha    text NOT NULL,
    name          text NOT NULL DEFAULT '',
    body          text NOT NULL DEFAULT '',
    is_prerelease boolean NOT NULL DEFAULT false,
    is_draft      boolean NOT NULL DEFAULT false,
    created_by    bigint NOT NULL REFERENCES users(id),
    created_at    timestamptz NOT NULL DEFAULT now(),
    UNIQUE (repo_id, tag_name)
);

CREATE TABLE release_assets (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    release_id   bigint NOT NULL REFERENCES releases(id) ON DELETE CASCADE,
    name         text NOT NULL,
    size_bytes   bigint NOT NULL,
    content_type text NOT NULL DEFAULT 'application/octet-stream',
    storage_path text NOT NULL,
    downloads    integer NOT NULL DEFAULT 0,
    created_at   timestamptz NOT NULL DEFAULT now(),
    UNIQUE (release_id, name)
);

CREATE TABLE repo_hooks (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repo_id    bigint NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    url        text NOT NULL,
    secret     text NOT NULL DEFAULT '',
    events     text NOT NULL DEFAULT 'push',              -- csv: push,pull_request,issues,release
    content_type text NOT NULL DEFAULT 'application/json',
    is_active  boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE hook_deliveries (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    hook_id     bigint NOT NULL REFERENCES repo_hooks(id) ON DELETE CASCADE,
    event       text NOT NULL,
    status_code integer,
    ok          boolean NOT NULL DEFAULT false,
    duration_ms integer NOT NULL DEFAULT 0,
    error       text NOT NULL DEFAULT '',
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_hook_deliveries_hook ON hook_deliveries (hook_id, created_at DESC);

UPDATE instance_info SET schema_phase = 6;
