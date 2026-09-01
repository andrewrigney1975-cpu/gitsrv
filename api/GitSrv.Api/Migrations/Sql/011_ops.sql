-- Phase 11: audit log, instance settings.

CREATE TABLE audit_events (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    org_id     bigint REFERENCES organisations(id) ON DELETE CASCADE,
    actor_id   bigint REFERENCES users(id) ON DELETE SET NULL,
    actor_name text NOT NULL DEFAULT '',
    action     text NOT NULL,                          -- login | token.create | member.add | repo.delete | secret.set | ...
    target     text NOT NULL DEFAULT '',
    detail     text NOT NULL DEFAULT '',
    ip         text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_audit_org ON audit_events (org_id, created_at DESC);
CREATE INDEX idx_audit_actor ON audit_events (actor_id, created_at DESC);

CREATE TABLE instance_settings (
    key   text PRIMARY KEY,
    value text NOT NULL
);
INSERT INTO instance_settings (key, value) VALUES
    ('registration_open', 'true'),
    ('default_repo_quota_mb', '0')       -- 0 = unlimited
ON CONFLICT DO NOTHING;

UPDATE instance_info SET schema_phase = 11;
