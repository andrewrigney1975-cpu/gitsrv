-- Phase 9: Enklr.app project-management integration (GitSrv side of the connector).

CREATE TABLE enklr_connections (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    org_id        bigint NOT NULL REFERENCES organisations(id) ON DELETE CASCADE,
    base_url      text NOT NULL,                        -- Enklr instance base URL
    workspace     text NOT NULL DEFAULT '',             -- Enklr workspace/board identifier
    api_token     text NOT NULL DEFAULT '',             -- bearer token GitSrv uses to call Enklr
    inbound_secret text NOT NULL DEFAULT '',            -- HMAC secret Enklr uses to call GitSrv
    card_prefix   text NOT NULL DEFAULT 'ENK',          -- reference keyword: ENK-123
    is_active     boolean NOT NULL DEFAULT true,
    created_by    bigint REFERENCES users(id),
    created_at    timestamptz NOT NULL DEFAULT now(),
    UNIQUE (org_id)
);

-- A discovered reference between a GitSrv object and an Enklr card.
CREATE TABLE enklr_links (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    connection_id bigint NOT NULL REFERENCES enklr_connections(id) ON DELETE CASCADE,
    repo_id      bigint NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    card_ref     text NOT NULL,                         -- e.g. ENK-123
    source_kind  text NOT NULL,                         -- pull | branch | commit
    source_ref   text NOT NULL,                         -- PR number / branch / sha
    title        text NOT NULL DEFAULT '',
    state        text NOT NULL DEFAULT '',              -- open | merged | closed | pending | success | failure
    url          text NOT NULL DEFAULT '',
    updated_at   timestamptz NOT NULL DEFAULT now(),
    created_at   timestamptz NOT NULL DEFAULT now(),
    UNIQUE (connection_id, card_ref, source_kind, source_ref)
);
CREATE INDEX idx_enklr_links_card ON enklr_links (connection_id, card_ref);

CREATE TABLE enklr_deliveries (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    connection_id bigint NOT NULL REFERENCES enklr_connections(id) ON DELETE CASCADE,
    direction   text NOT NULL,                          -- out | in
    event       text NOT NULL,
    status_code integer,
    ok          boolean NOT NULL DEFAULT false,
    detail      text NOT NULL DEFAULT '',
    created_at  timestamptz NOT NULL DEFAULT now()
);

UPDATE instance_info SET schema_phase = 9;
