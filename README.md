# GitSrv

A self-hosted, multi-tenant Git platform — organisations, teams, unlimited repositories, full Git
over HTTP and SSH, pull requests with three-way compare, branch visualisation, commit history and
blame. Lightweight enough to run on a Raspberry Pi 3.

**Stack:** PostgreSQL 16 · ASP.NET Core / C# API · no-framework HTML5/CSS3/ES-module front end ·
each tier in its own container. Visual identity inherited from [Enklr.app](https://enklr.app).

See [`docs/PLAN.md`](docs/PLAN.md) for the twelve-phase build plan.

## Status

**All 12 phases complete.** GitSrv is a working multi-tenant Git platform: full Git over HTTP and
SSH, repository browsing, pull requests with three-way compare, issues, notifications, branch
protection, releases, webhooks, GitHub-flavoured CI, npm/OCI/generic package registries, an
Enklr.app connector, and an ops surface (audit log, `/metrics`, admin console, backup/restore).
**54 black-box contract tests + 46 unit tests**, all green on a clean-host install. See
[`CHANGELOG.md`](CHANGELOG.md), [`docs/INSTALL.md`](docs/INSTALL.md),
[`docs/UPGRADING.md`](docs/UPGRADING.md), [`docs/SECURITY.md`](docs/SECURITY.md).

**Repository import.** Create a repo from a public clone URL on another host (`POST
/api/orgs/{slug}/repos/import`, "Import" on the org page): the source is SSRF-checked, then
`RepoImportWorker` mirrors it with `git clone --bare` in the background and applies GitSrv's repo
config, hooks and commit-graph. Progress and failures show on the repo page (migration 012).

**Phase 11 — security, operability & release.** Org-scoped audit log with CSV export; per-IP rate
limiting on login; SSRF guard on webhook/integration URLs (blocks loopback/private/metadata
ranges); `GET /metrics` (Prometheus); an admin-console API (instance overview, user/org management,
site-admin toggle, `registration_open` flag); `scripts/backup.sh` + `scripts/restore.sh` (verified
drill); a Caddy TLS production overlay; install / upgrade / security docs.

**Phase 10 — performance & scale hardening.** Rendered-Markdown cache (content-hashed,
`IMemoryCache`, 64 MiB cap); strong ETag + `Cache-Control` on blob/raw reads (304 on match).
Bare repos are created with reachability bitmaps, commit-graph and MIDX enabled; a
`GitMaintenanceWorker` background service runs incremental repack / commit-graph / multi-pack-index
on repos pushed since last maintained. Bounded concurrent `upload-pack`
(`GitSrv:MaxConcurrentFetches`). N+1 sweep on the issue list; composite indexes for the PR-sync,
checks-gate and inbox hot paths (migration 010). Route-level code splitting — only core views load
eagerly, the rest are dynamic `import()`.

**Load budgets** (verified on Docker Desktop with `GITSRV_LOAD=1`): 15 concurrent clones of a
40-commit repo p95 &lt; 15 s (measured ~0.5 s); 75 concurrent browse reads p95 &lt; 2 s
(measured ~0.2 s). Raspberry Pi 3 verification is a manual step — the profile is
`contract-tests/load.test.mjs`.

**Phase 9 — Enklr.app integration.** GitSrv's side of the connector: per-org connection to an
Enklr workspace; `ENK-123` card references in commits / branches / PRs are discovered, linked, and
pushed to Enklr (`POST {base}/api/gitsrv/refs` and `/events`, bearer-authenticated) so a card shows
its linked work and moves on merge. CI verdicts propagate to the card. Reverse direction: an
HMAC-verified inbound webhook (`/api/integrations/enklr/{id}/events`). A `GET .../enklr/cards/{ref}`
endpoint lets Enklr render the linked branches / PRs / status.

**Phase 8 — package registry.** Org-scoped registries behind a storage abstraction
(`IArtifactStore`, local-volume driver; S3 slots in later): an **npm** registry
(`.npmrc` → `{base}/npm/{org}/`), an **OCI/Docker** registry (`/v2/`, blob upload flow, manifests,
tags — `docker push {host}/{org}/{image}`), and a **generic** file registry. Auth via personal
access tokens; per-package visibility (public/internal/private) overriding the default. Web UI:
per-org package list with storage usage, and a package page with versions, files and copy-paste
install instructions. NuGet/PyPI/Maven/Cargo/etc. follow the same pattern and can be added
incrementally.

**Phase 7 — GitSrv Actions (CI/CD).** A GitHub-Actions-flavoured subset: `.gitsrv/workflows/*.yml`
with `on` (push / pull_request + branch filters), `jobs` with `runs-on` / `container` / `needs` /
`env`, `strategy.matrix`, and `run` / `uses` (checkout) steps. Push and pull_request events dispatch
runs; a poller container (`runner`) executes each job's steps in a scratch container over the host
Docker socket, streams logs back, and posts a commit status per job. Repo and org secrets
(AES-GCM at rest) are injected as env and masked in logs. Branch protection can require status
checks — a PR's merge button then stays disabled until every check on the head sha is green.

**Phase 6 — advanced Git ops & branch policy.** Branch protection (require PR, N approvals, block
force-push/deletion, linear history, restrict direct pushes) enforced by a `pre-receive` hook in
every bare repo that calls back to the API — so a protected `main` rejects a direct push over both
transports. Web-initiated ops via libgit2, no worktree: create/rename/delete branch, cherry-pick,
revert, edit a file and commit. Releases with annotated tags and uploadable binary assets. Repo
webhooks (HMAC-signed, delivery log) fired from the `post-receive` hook. Repo config for default
branch and allowed merge methods.

**Phase 5 — issues, notifications, activity.** Issues share a per-repo number space with PRs, so
`#5` is unambiguous. Labels, milestones, assignees, comments, a state timeline, and cross-references
from PRs/commits (`closes #N` in a merged PR closes the issue). Shared Markdown pipeline: task
lists, tables, emoji, and `#N` / `@user` autolinking. Notifications (mention / assign / author /
watch / comment) land in an in-app inbox with an unread badge; a background worker delivers email
digests over SMTP (a `mailpit` container catches them in QA). Activity feeds per repo, per org, and
on the personal dashboard.

**Phase 4 — pull requests.** Same-repo cross-branch PRs with a libgit2 three-way compare
(merge-base, ahead/behind, conflict detection on trees, rename detection). Inline review threads
with pending comments that publish on review submission; review states (comment / approve / request
changes); resolvable threads. Merge via merge-commit, squash or rebase — executed entirely through
libgit2, no worktree — gated on conflicts, draft status and outstanding change requests, with
optional head-branch auto-delete. Pushing the base branch past a PR's head auto-merges it; deleting
the head branch auto-closes it. Web UI: PR list, compare/new-PR screen, and a detail view with
conversation, commits, a files-changed diff with click-to-comment, a review bar and a merge box.

**Phase 3 — repository browsing.** libgit2-backed read API: ref list, tree, blob (binary/size
guards), raw download, paginated + per-path commit history, commit detail with unified diff,
line-level blame with age heat, and a lane-assigned commit graph. Repo home renders the README
(Markdown → sanitised HTML) and a language bar. Web UI: file tree, breadcrumb, branch/tag picker,
file view with lazy syntax highlighting (highlight.js), commit list, diff view, blame, and a Canvas
commit graph. Public repos are browsable without an account.

**Phase 2 — Git transport.** Full Git over HTTPS and SSH. Bare repos are created on the shared
volume when a repo record is made (`{root}/{orgId}/{repoId}.git`, self-healing). Smart-HTTP
(`info/refs`, `git-upload-pack`, `git-receive-pack`) streams straight through `git`; auth is HTTP
Basic with a personal access token (scoped read/write) or the account password; anonymous
fetch/clone works for public repos. The `ssh` container resolves keys and authorizes every
operation through the API, so `git@host:org/repo.git` enforces the same `PermissionResolver` as
HTTP. Per-push size limit and repo size accounting.

Earlier phases: **1** — orgs, members, teams, repo records, collaborator/team grants, Argon2id
passwords, JWT + refresh cookies, SSH key management, slug redirects, and the web admin UI. **0** —
four-container stack, migration runner, design-token port, CI.

## Quick start

```sh
cp .env.example .env          # edit DB_PASSWORD / JWT_SIGNING_KEY
docker compose up --build
```

Then open <http://localhost:8080>. `GET /health` should report `{"status":"ok","db":"ok"}`.

## Layout

| Path              | Tier / purpose                                              |
|-------------------|------------------------------------------------------------|
| `api/`            | ASP.NET Core API, SQL migrations, migration runner          |
| `web/`            | Static front end + nginx config and image                   |
| `db/`             | Database notes (stock Postgres image; no custom build)      |
| `ssh/`            | Git-over-SSH transport (Phase 0 stub; real in Phase 2)      |
| `contract-tests/` | Black-box tests against a running stack (`npm test`)        |
| `docs/`           | Build plan and, later, install / API / integrator guides    |

## Development

- API alone: `dotnet run --project api/GitSrv.Api` (uses `appsettings.Development.json`; needs a
  local Postgres, or point `ConnectionStrings__Default` at the compose `db` on `localhost:5432`).
- Contract tests against a running stack: `cd contract-tests && npm test`.

## Front-end conventions (Phase 0 decision, held to as the UI grows)

- No framework, no bundler runtime. ES modules only.
- Feature modules live in `web/src/js/features/` and reach the server **only** through
  `web/src/js/api.js`.
- All colour and spacing comes from `--gs-*` tokens in `web/src/css/gs-tokens.css`. Both themes,
  system-aware, applied before first paint.
