-- Phase 7: GitSrv Actions. Workflow runs, jobs, steps, logs, artifacts, secrets, commit statuses.

CREATE TABLE workflow_runs (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repo_id      bigint NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    number       integer NOT NULL,                      -- per-repo run number
    workflow_name text NOT NULL,
    workflow_path text NOT NULL,
    event        text NOT NULL,                         -- push | pull_request
    ref          text NOT NULL,
    head_sha     text NOT NULL,
    pr_number    integer,
    triggered_by bigint REFERENCES users(id),
    status       text NOT NULL DEFAULT 'queued' CHECK (status IN ('queued','running','completed')),
    conclusion   text CHECK (conclusion IN ('success','failure','cancelled')),
    created_at   timestamptz NOT NULL DEFAULT now(),
    started_at   timestamptz,
    completed_at timestamptz,
    UNIQUE (repo_id, number)
);
CREATE INDEX idx_workflow_runs_repo ON workflow_runs (repo_id, created_at DESC);
CREATE INDEX idx_workflow_runs_sha ON workflow_runs (head_sha);

CREATE TABLE workflow_jobs (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    run_id       bigint NOT NULL REFERENCES workflow_runs(id) ON DELETE CASCADE,
    name         text NOT NULL,
    runs_on      text NOT NULL DEFAULT 'ubuntu-latest',
    matrix_json  text NOT NULL DEFAULT '{}',
    needs_json   text NOT NULL DEFAULT '[]',
    status       text NOT NULL DEFAULT 'queued' CHECK (status IN ('queued','running','completed','skipped')),
    conclusion   text CHECK (conclusion IN ('success','failure','cancelled','skipped')),
    runner_id    text,
    started_at   timestamptz,
    completed_at timestamptz,
    created_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_workflow_jobs_run ON workflow_jobs (run_id);
CREATE INDEX idx_workflow_jobs_queued ON workflow_jobs (status) WHERE status = 'queued';

CREATE TABLE job_steps (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    job_id      bigint NOT NULL REFERENCES workflow_jobs(id) ON DELETE CASCADE,
    number      integer NOT NULL,
    name        text NOT NULL,
    kind        text NOT NULL DEFAULT 'run',            -- run | uses | checkout
    spec_json   text NOT NULL DEFAULT '{}',
    status      text NOT NULL DEFAULT 'queued',
    conclusion  text,
    exit_code   integer,
    started_at  timestamptz,
    completed_at timestamptz,
    UNIQUE (job_id, number)
);

CREATE TABLE job_logs (
    id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    job_id   bigint NOT NULL REFERENCES workflow_jobs(id) ON DELETE CASCADE,
    step_number integer,
    seq      bigint NOT NULL,
    line     text NOT NULL,
    at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_job_logs_job ON job_logs (job_id, seq);

CREATE TABLE run_artifacts (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    run_id      bigint NOT NULL REFERENCES workflow_runs(id) ON DELETE CASCADE,
    name        text NOT NULL,
    size_bytes  bigint NOT NULL,
    storage_path text NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    UNIQUE (run_id, name)
);

-- Commit statuses (checks). Populated by workflow runs and postable via the API.
CREATE TABLE commit_statuses (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repo_id     bigint NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    sha         text NOT NULL,
    context     text NOT NULL,
    state       text NOT NULL CHECK (state IN ('pending','success','failure','error')),
    description text NOT NULL DEFAULT '',
    target_url  text NOT NULL DEFAULT '',
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    UNIQUE (repo_id, sha, context)
);
CREATE INDEX idx_commit_statuses_sha ON commit_statuses (repo_id, sha);

CREATE TABLE action_secrets (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    scope      text NOT NULL CHECK (scope IN ('repo','org')),
    owner_id   bigint NOT NULL,                          -- repo_id or org_id
    name       text NOT NULL,
    value_enc  text NOT NULL,                            -- AES-GCM, base64
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (scope, owner_id, name)
);

-- Runner registration + short-lived job tokens.
CREATE TABLE job_tokens (
    token_hash text PRIMARY KEY,
    job_id     bigint NOT NULL REFERENCES workflow_jobs(id) ON DELETE CASCADE,
    expires_at timestamptz NOT NULL
);

ALTER TABLE branch_protections ALTER COLUMN require_status_checks SET DEFAULT false;

UPDATE instance_info SET schema_phase = 7;
