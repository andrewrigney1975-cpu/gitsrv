-- Phase 8: artifact / package registry (org-scoped).

CREATE TABLE packages (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    org_id      bigint NOT NULL REFERENCES organisations(id) ON DELETE CASCADE,
    kind        text NOT NULL,                        -- npm | nuget | pypi | maven | oci | generic
    name        text NOT NULL,                        -- ecosystem-native name (npm scope kept, oci repo path)
    visibility  text NOT NULL DEFAULT 'private' CHECK (visibility IN ('public','internal','private')),
    created_by  bigint REFERENCES users(id),
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    UNIQUE (org_id, kind, name)
);
CREATE INDEX idx_packages_org ON packages (org_id);

CREATE TABLE package_versions (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    package_id  bigint NOT NULL REFERENCES packages(id) ON DELETE CASCADE,
    version     text NOT NULL,
    metadata    text NOT NULL DEFAULT '{}',           -- ecosystem-native metadata JSON
    yanked      boolean NOT NULL DEFAULT false,
    published_by bigint REFERENCES users(id),
    created_at  timestamptz NOT NULL DEFAULT now(),
    UNIQUE (package_id, version)
);
CREATE INDEX idx_package_versions_pkg ON package_versions (package_id);

-- One physical file (tarball, wheel, jar, blob, manifest). digest is the content-addressed key.
CREATE TABLE package_files (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    package_id   bigint NOT NULL REFERENCES packages(id) ON DELETE CASCADE,
    version_id   bigint REFERENCES package_versions(id) ON DELETE CASCADE,
    name         text NOT NULL,
    digest       text NOT NULL,                       -- sha256:hex
    size_bytes   bigint NOT NULL,
    content_type text NOT NULL DEFAULT 'application/octet-stream',
    storage_key  text NOT NULL,
    downloads    integer NOT NULL DEFAULT 0,
    created_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_package_files_pkg ON package_files (package_id);
CREATE INDEX idx_package_files_digest ON package_files (package_id, digest);

-- OCI (Docker) needs mutable tag -> manifest-digest pointers and in-progress blob uploads.
CREATE TABLE oci_tags (
    package_id  bigint NOT NULL REFERENCES packages(id) ON DELETE CASCADE,
    tag         text NOT NULL,
    manifest_digest text NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (package_id, tag)
);

CREATE TABLE oci_uploads (
    uuid        text PRIMARY KEY,
    package_id  bigint NOT NULL REFERENCES packages(id) ON DELETE CASCADE,
    storage_key text NOT NULL,
    offset_bytes bigint NOT NULL DEFAULT 0,
    created_at  timestamptz NOT NULL DEFAULT now()
);

UPDATE instance_info SET schema_phase = 8;
