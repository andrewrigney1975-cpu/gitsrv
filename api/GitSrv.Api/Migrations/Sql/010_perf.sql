-- Phase 10: indexes for hot read paths + a maintenance bookkeeping column.

ALTER TABLE repositories ADD COLUMN last_maintained_at timestamptz;

-- SyncAfterPush and the pull_request event dispatch both filter open PRs by (repo, head_branch).
CREATE INDEX IF NOT EXISTS idx_pr_repo_head_state ON pull_requests (repo_id, head_branch, state);

-- Issue/PR number lookups and the checks gate.
CREATE INDEX IF NOT EXISTS idx_commit_statuses_sha_state ON commit_statuses (repo_id, sha, state);

-- Notification inbox unread scan is already covered by idx_notif_user; add a covering one for the
-- unread count endpoint.
CREATE INDEX IF NOT EXISTS idx_notif_user_unread_only ON notifications (user_id) WHERE NOT is_read;

-- Package browse by org + kind.
CREATE INDEX IF NOT EXISTS idx_packages_org_kind ON packages (org_id, kind);

-- Blame / log per-path history walks hit package_files & job_logs ordering.
ANALYZE;

UPDATE instance_info SET schema_phase = 10;
