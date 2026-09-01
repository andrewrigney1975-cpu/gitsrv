-- Phase 4: pull requests, review threads, merge.

CREATE TABLE pull_requests (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repo_id     bigint NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    number      integer NOT NULL,                         -- per-repo, user-facing
    title       text NOT NULL,
    body        text NOT NULL DEFAULT '',
    state       text NOT NULL DEFAULT 'open' CHECK (state IN ('open', 'merged', 'closed')),
    is_draft    boolean NOT NULL DEFAULT false,
    base_branch text NOT NULL,
    head_branch text NOT NULL,
    head_sha    text NOT NULL,                            -- head tip at last sync
    merge_sha   text,
    merge_method text CHECK (merge_method IN ('merge', 'squash', 'rebase')),
    merged_by   bigint REFERENCES users(id),
    merged_at   timestamptz,
    closed_at   timestamptz,
    created_by  bigint NOT NULL REFERENCES users(id),
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    UNIQUE (repo_id, number)
);
CREATE INDEX idx_pr_repo_state ON pull_requests (repo_id, state);

-- Per-repo PR counter, bumped transactionally when a PR is opened.
CREATE TABLE repo_pr_counters (
    repo_id bigint PRIMARY KEY REFERENCES repositories(id) ON DELETE CASCADE,
    last_number integer NOT NULL DEFAULT 0
);

CREATE TABLE pr_reviewers (
    pr_id   bigint NOT NULL REFERENCES pull_requests(id) ON DELETE CASCADE,
    user_id bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    PRIMARY KEY (pr_id, user_id)
);

CREATE TABLE pr_reviews (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    pr_id      bigint NOT NULL REFERENCES pull_requests(id) ON DELETE CASCADE,
    user_id    bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    state      text NOT NULL CHECK (state IN ('comment', 'approve', 'request_changes')),
    body       text NOT NULL DEFAULT '',
    commit_sha text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_pr_reviews_pr ON pr_reviews (pr_id);

-- A review-comment thread anchored to a file (optionally a line). Comments hang off it.
CREATE TABLE pr_threads (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    pr_id       bigint NOT NULL REFERENCES pull_requests(id) ON DELETE CASCADE,
    file_path   text NOT NULL,
    line        integer,                                  -- null = file-level
    side        text NOT NULL DEFAULT 'new' CHECK (side IN ('old', 'new')),
    is_resolved boolean NOT NULL DEFAULT false,
    resolved_by bigint REFERENCES users(id),
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_pr_threads_pr ON pr_threads (pr_id);

CREATE TABLE pr_comments (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    pr_id      bigint NOT NULL REFERENCES pull_requests(id) ON DELETE CASCADE,
    thread_id  bigint REFERENCES pr_threads(id) ON DELETE CASCADE,  -- null = conversation comment
    review_id  bigint REFERENCES pr_reviews(id) ON DELETE SET NULL, -- set when part of a submitted review
    user_id    bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    body       text NOT NULL,
    is_pending boolean NOT NULL DEFAULT false,            -- drafted, not yet submitted
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_pr_comments_pr ON pr_comments (pr_id);

-- Merge behaviour, per repo (protected-branch policy lands in Phase 6).
ALTER TABLE repositories ADD COLUMN allow_merge_commit boolean NOT NULL DEFAULT true;
ALTER TABLE repositories ADD COLUMN allow_squash boolean NOT NULL DEFAULT true;
ALTER TABLE repositories ADD COLUMN allow_rebase boolean NOT NULL DEFAULT true;
ALTER TABLE repositories ADD COLUMN delete_branch_on_merge boolean NOT NULL DEFAULT true;

UPDATE instance_info SET schema_phase = 4;
