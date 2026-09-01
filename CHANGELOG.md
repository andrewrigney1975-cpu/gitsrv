# Changelog

All development to date, by build phase. Dates are the day the phase landed on `main`.

## Phase 11 — security, operability & release — 2026-09-01
- Org-scoped audit log (`audit_events`) + CSV export; entries for login, member changes, repo delete.
- Rate limiting (20/min per IP on `/api/auth/*`); `ForwardedHeaders` for correct client IP behind the proxy.
- SSRF guard (`Ops/UrlGuard`) on webhook + Enklr callback URLs — blocks loopback / private / metadata addresses.
- `GET /metrics` (Prometheus text). Admin console API: instance overview, user/org lists, site-admin toggle, `registration_open` flag.
- `scripts/backup.sh` + `scripts/restore.sh`; `deploy/docker-compose.prod.yml` (Caddy TLS overlay); install / upgrade / security docs.

## Phase 10 — performance & scale hardening — 2026-09-01
- Rendered-Markdown cache; ETag/Cache-Control + 304 on blob reads.
- Bare repos with bitmaps + commit-graph + MIDX; `GitMaintenanceWorker` background repack.
- Bounded concurrent `upload-pack`; N+1 sweep on the issue list; composite indexes (migration 010).
- Route-level code splitting in the front end.

## Phase 9 — Enklr.app integration — 2026-09-01
- Per-org Enklr connection; `ENK-123` reference discovery in commits / branches / PRs; outbound ref + lifecycle events; CI verdict propagation; HMAC-verified inbound webhook.

## Phase 8 — package registry — 2026-09-01
- `IArtifactStore` abstraction; npm, OCI/Docker and generic registries, org-scoped, PAT-authed, per-package visibility.

## Phase 7 — GitSrv Actions — 2026-09-01
- Workflow subset (`on`, `jobs`, `matrix`, `run`/`uses`), poller `runner` container, live logs, commit statuses, repo/org secrets, required-status-check merge gate.

## Phase 6 — advanced Git ops & branch policy — 2026-09-01
- Branch protection via a pre-receive hook; web cherry-pick / revert / edit-file / branch CRUD; releases + assets; repo webhooks.

## Phase 5 — issues, notifications, activity — 2026-09-01
- Issues (labels, milestones, assignees, timeline), shared Markdown, `#N`/`@user` autolinks, `closes #N`, in-app inbox + email digests, activity feeds.

## Phase 4 — pull requests — 2026-09-01
- Three-way compare, inline review threads with pending comments, merge / squash / rebase via libgit2, push-driven auto-close/merge.

## Phase 3 — repository browsing — 2026-09-01
- libgit2 read API (tree, blob, history, blame, commit graph), README render, language bar, syntax highlighting.

## Phase 2 — Git transport — 2026-09-01
- Smart-HTTP + SSH, bare-repo storage, personal access tokens, per-push limits.

## Phase 1 — identity — 2026-09-01
- Orgs, members, teams, repos, collaborator/team grants, `PermissionResolver` core, sessions, SSH keys, slug redirects.

## Phase 0 — foundations — 2026-09-01
- Four-container stack, SQL migration runner, Enklr design-token port, CI.
