-- Phase 5: issues, labels, milestones, cross-references, notifications, activity.

-- Issues and PRs share one per-repo number space so '#5' is unambiguous.
ALTER TABLE repo_pr_counters RENAME TO repo_number_seq;

CREATE TABLE labels (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repo_id     bigint NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    name        text NOT NULL,
    color       text NOT NULL DEFAULT '#0c66e4',
    description text NOT NULL DEFAULT '',
    UNIQUE (repo_id, name)
);

CREATE TABLE milestones (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repo_id     bigint NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    title       text NOT NULL,
    description text NOT NULL DEFAULT '',
    due_on      date,
    state       text NOT NULL DEFAULT 'open' CHECK (state IN ('open', 'closed')),
    created_at  timestamptz NOT NULL DEFAULT now(),
    UNIQUE (repo_id, title)
);

CREATE TABLE issues (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repo_id      bigint NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    number       integer NOT NULL,
    title        text NOT NULL,
    body         text NOT NULL DEFAULT '',
    state        text NOT NULL DEFAULT 'open' CHECK (state IN ('open', 'closed')),
    milestone_id bigint REFERENCES milestones(id) ON DELETE SET NULL,
    created_by   bigint NOT NULL REFERENCES users(id),
    closed_by    bigint REFERENCES users(id),
    closed_at    timestamptz,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    UNIQUE (repo_id, number)
);
CREATE INDEX idx_issues_repo_state ON issues (repo_id, state);

CREATE TABLE issue_labels (
    issue_id bigint NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
    label_id bigint NOT NULL REFERENCES labels(id) ON DELETE CASCADE,
    PRIMARY KEY (issue_id, label_id)
);

CREATE TABLE issue_assignees (
    issue_id bigint NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
    user_id  bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    PRIMARY KEY (issue_id, user_id)
);

CREATE TABLE issue_comments (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    issue_id   bigint NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
    user_id    bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    body       text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_issue_comments_issue ON issue_comments (issue_id);

-- Timeline: opened / closed / reopened / labeled / unlabeled / assigned / unassigned /
-- milestoned / referenced / renamed.
CREATE TABLE issue_events (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    issue_id   bigint NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
    actor_id   bigint REFERENCES users(id) ON DELETE SET NULL,
    kind       text NOT NULL,
    detail     text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_issue_events_issue ON issue_events (issue_id);

-- Cross references from commits / PRs / comments to an issue.
CREATE TABLE issue_references (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    issue_id    bigint NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
    source_kind text NOT NULL,                             -- commit | pr | issue | comment
    source_ref  text NOT NULL,
    closes      boolean NOT NULL DEFAULT false,
    created_at  timestamptz NOT NULL DEFAULT now(),
    UNIQUE (issue_id, source_kind, source_ref)
);

CREATE TABLE repo_watches (
    repo_id bigint NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    user_id bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    reason  text NOT NULL DEFAULT 'manual',                -- manual | auto
    PRIMARY KEY (repo_id, user_id)
);

CREATE TABLE notifications (
    id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id        bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    repo_id        bigint REFERENCES repositories(id) ON DELETE CASCADE,
    subject_kind   text NOT NULL,                          -- issue | pull
    subject_number integer,
    title          text NOT NULL,
    reason         text NOT NULL,                          -- mention | assign | author | watch | review_request | comment | closed
    body           text NOT NULL DEFAULT '',
    url            text NOT NULL DEFAULT '',
    is_read        boolean NOT NULL DEFAULT false,
    email_sent     boolean NOT NULL DEFAULT false,
    created_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_notif_user ON notifications (user_id, is_read, created_at DESC);
CREATE INDEX idx_notif_pending_email ON notifications (created_at) WHERE NOT email_sent;

CREATE TABLE activity (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    actor_id   bigint REFERENCES users(id) ON DELETE SET NULL,
    org_id     bigint REFERENCES organisations(id) ON DELETE CASCADE,
    repo_id    bigint REFERENCES repositories(id) ON DELETE CASCADE,
    kind       text NOT NULL,
    ref_number integer,
    summary    text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_activity_repo ON activity (repo_id, created_at DESC);
CREATE INDEX idx_activity_org ON activity (org_id, created_at DESC);
CREATE INDEX idx_activity_actor ON activity (actor_id, created_at DESC);

UPDATE instance_info SET schema_phase = 5;
