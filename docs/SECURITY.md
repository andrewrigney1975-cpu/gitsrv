# GitSrv security model

## Authentication

- Web: Argon2id passwords; short-lived JWT access cookie (`gs_access`, HttpOnly, SameSite=Lax) +
  rotating opaque refresh token (`gs_refresh`, only its SHA-256 hash stored). Changing a password
  revokes every other session.
- Git / registries: HTTP Basic or Bearer with a **personal access token** (`gsp_…`, SHA-256 hashed,
  scoped read/write). SSH: public key → `AuthorizedKeysCommand` asks the API whose key it is, then a
  forced command re-checks authorization before exec'ing the pack program.
- CSRF: state-changing `/api` calls that present a session cookie must also carry a custom
  `X-GitSrv-CSRF` header (a header a cross-origin page cannot set; CORS is not enabled).
- Rate limiting: 20 req/min per client IP on `/api/auth/*`.

## Authorization

Every API and Git path funnels through `Authz/Authorizer.cs`, which loads the facts and defers to
the pure, unit-tested `Authz/PermissionResolver.cs`
(`None < Read < Triage < Write < Maintain < Admin`, max across grant paths, archived repos clamped
to Read). Endpoints never query membership tables directly. A repo the caller can't read 404s
rather than 403s.

## Untrusted-input surfaces

| Surface | Guard |
|---|---|
| Slugs / repo paths | validated (`Identity/Slug.cs`), reserved-word list; on-disk paths are built from integer IDs, never user strings |
| Webhook + Enklr callback URLs | `Ops/UrlGuard.EnsureSafe` rejects loopback / private / link-local / cloud-metadata (169.254.169.254) addresses (relaxed only when `GitSrv:AllowPrivateWebhookHosts=true`, for local/QA) |
| Markdown (READMEs, comments) | Markdig with raw HTML **disabled**; `#N` / `@user` linkified on text nodes only |
| Raw file downloads | served `application/octet-stream` with `Content-Disposition: attachment` so browsers never render them inline; nginx sends `X-Content-Type-Options: nosniff` and a restrictive CSP |
| Action secrets | AES-GCM at rest (`GitSrv:SecretsKey`); values never returned by any API; masked in job logs by the runner |
| Pre-receive policy | enforced by a hook in every bare repo that calls back to the API — libgit2 merges (which bypass receive-pack) are the API's own, already-authorized operations |

## Known trade-offs (hardening backlog)

- **Actions runner uses the host Docker socket** (Docker-out-of-Docker) and mounts it into step
  containers via `--volumes-from`. Job code can therefore reach the host daemon. Acceptable for a
  trusted-team instance; isolate the runner (rootless / sysbox / a dedicated VM) before running
  untrusted workflows.
- `/metrics` is unauthenticated — put it behind the proxy or an IP allowlist.
- TLS is terminated by the proxy, not the stack; `docker-compose.yml` alone is HTTP-only.

## Reporting

Open a private security advisory on the repository, or email the maintainer. Describe the class of
issue, not a working exploit.
