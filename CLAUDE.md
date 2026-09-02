# GitSrv — notes for Claude

Multi-tenant self-hosted Git platform. See `docs/PLAN.md` for the phased plan and `README.md` for
layout and quick start.

## Architecture

- **db** — stock `postgres:16-alpine`. Relational metadata only; Git objects live on the
  `git-data` volume as bare repos, never in the database.
- **api** — `api/GitSrv.Api`, ASP.NET Core minimal APIs on .NET 10, C#. Uses raw `Npgsql` (no EF).
  Runs SQL migrations on start-up.
- **web** — `web/src`, static no-framework ES modules, served by nginx (`web/nginx.conf`). nginx
  proxies `/api` and `/health` to `api:8080`.
- **ssh** — `ssh/`, Phase 0 stub. Real Git-over-SSH transport lands in Phase 2.

## Migrations

Ordered SQL in `api/GitSrv.Api/Migrations/Sql/` (`001_init.sql`, `002_*.sql`, …). Forward-only.
Applied once each, in a transaction, recorded in `schema_migrations` with a checksum — **editing an
applied migration file breaks start-up**; add a new file instead.

## Authorization (Phase 1)

`Authz/PermissionResolver.cs` is the pure, unit-tested core: given `RepoAccessFacts` (site-admin,
org role, visibility, archived, direct + team grants) it returns an effective `RepoPermission`
(None<Read<Triage<Write<Maintain<Admin) as the max across grant paths. `Authz/Authorizer.cs`
loads the facts from the DB and is the ONLY place endpoints ask "can this user do X?" — endpoints
never query membership tables directly. Dapper maps snake_case→PascalCase constructor params via
`Data/DapperSetup.cs` (register new DB record types there).

## Git transport (Phase 2)

- On-disk: bare repos at `{RepositoryRoot}/{orgId}/{repoId}.git` — keyed by id so renames never
  move a directory. `Git/GitStorage.cs` owns creation (idempotent `EnsureAsync`, self-heals) /
  deletion / size. The api and ssh containers share the `git-data` volume; both run as uid 1654.
- HTTP: `Endpoints/GitHttpEndpoints.cs` at the clone-URL root (`/{org}/{repo}.git/...`), streamed
  through `Git/GitBackend.cs`. nginx routes `~ \.git` there with buffering off.
- SSH: `ssh/` container. sshd `AuthorizedKeysCommand` → `/internal/ssh/authorized-keys`; forced
  command `gitsrv-shell` → `/internal/ssh/authorize` (shared-secret `X-Internal-Token`, set by
  `INTERNAL_TOKEN`). Internal endpoints are network-only (no nginx route, no host port).
- Both transports funnel through `Git/GitAccessService.ResolveAsync` for path resolution + authz.
- PATs: `Auth/PatService.cs`, format `gsp_<64hex>`, sha256-hashed, scoped read/write.

## Repository browsing (Phase 3)

- `Git/RepoReader.cs` — libgit2 (LibGit2Sharp) read model: refs, tree, blob, log, commit+diff,
  blame, and the commit-graph lane assignment. Constructed per request, disposed after.
- `Git/RepoBrowseService.cs` — resolves org/repo (+ redirects), computes permission (anonymous
  gets Read on public repos via `Authorizer.GetRepoPermissionAsync(long?, …)`), opens a RepoReader.
- `Endpoints/RepoBrowseEndpoints.cs` — `/api/orgs/{slug}/repos/{repoSlug}/browse/*`, NOT behind
  RequireAuth. README → HTML via `Git/MarkdownRenderer.cs` (raw HTML disabled).
- Web: `views/repo.js` (shell + code/blob/blame), `views/repo-history.js` (commits/commit/graph),
  `views/repo-settings.js`. `features/highlight.js` lazy-loads highlight.js from cdnjs (allowed in
  the nginx CSP). Router (`router.js`) supports `:param` and a trailing `*rest`.

## Pull requests (Phase 4)

- `Git/RepoReader.Compare(baseRef, headRef)` — merge-base, commits ahead/behind, merge-base→head
  diff, and tree-only conflict detection (`ObjectDatabase.MergeCommits`).
- `Git/PrMergeService.cs` — merge/squash/rebase done purely in libgit2 (merged tree →
  `CreateCommit` → `Refs.UpdateTarget`), no working tree. Serialised per repo by
  `PullRequestService`'s `SemaphoreSlim` map.
- `Git/PullRequestService.cs` — PR lifecycle, review threads (pending comments visible only to
  their author until a review is submitted), merge gating, and `SyncAfterPushAsync` (called from
  both git transports after receive-pack: closes PRs whose head branch is gone, marks merged when
  base contains head).
- `Endpoints/PullRequestEndpoints.cs` — `/api/orgs/{slug}/repos/{repoSlug}/pulls/*`. GET is
  Read (anon on public); comments/reviews need Triage; create/merge need Write.
- Web: `views/pulls.js`. Cross-fork PRs are not implemented (no fork feature yet).

## Issues & collaboration (Phase 5)

- Issues + PRs draw from one per-repo sequence: `Data/RepoNumbers.NextAsync` (table
  `repo_number_seq`, renamed from `repo_pr_counters` in migration 005).
- `Collab/IssueService.cs` — issues, labels, milestones, assignees, timeline events, and
  `LinkAndMaybeCloseAsync` (called from `PullRequestService` on create — link only — and on merge —
  link + close). `Collab/NotificationService.cs` — inbox + `@mention` / watcher resolution.
  `Collab/ActivityService.cs` — repo/org/user feeds. `Collab/EmailWorker.cs` — `BackgroundService`
  polling unsent notifications, one digest per user per poll, SMTP via `Smtp:*` config (no-ops with
  a log line when unset).
- `Git/TextRefs.cs` + `MarkdownRenderer.ToCommentHtml` — `#N` / `@user` extraction and linkifying
  (linkify runs on text nodes only, never inside tags).
- Endpoints: `Endpoints/IssueEndpoints.cs` (`/issues`, `/labels`, `/milestones`, `/watch`,
  `/activity` under the repo), `Endpoints/NotificationEndpoints.cs` (`/api/notifications`),
  `ActivityEndpoints` (`/api/user/feed`, `/api/orgs/{slug}/activity`).
- `mail` service (mailpit) in compose; UI on `MAIL_UI_PORT` (default 8025).

## Advanced Git ops & branch policy (Phase 6)

- `GitStorage.WriteHooks` installs `pre-receive` / `post-receive` shell scripts in every bare repo
  (rewritten on every `EnsureAsync`). They inherit `GITSRV_API_BASE` / `GITSRV_INTERNAL_TOKEN` /
  `GITSRV_REPO_ID` / `GITSRV_PUSHER_ID` from the container that ran receive-pack (api sets them in
  `GitHttpEndpoints.Rpc`; ssh's `gitsrv-shell.sh` exports them) and POST line-oriented text to
  `Endpoints/InternalHookEndpoints.cs`. Pre-receive → `BranchProtectionService.EvaluatePushAsync`
  → `allow` / `deny\n<reason>`. Post-receive → activity + PR sync + `WebhookService.DeliverAsync`.
- `Git/BranchOpsService.cs` — branch CRUD, cherry-pick, revert, edit/delete file, all via libgit2
  ref updates (no worktree); they bypass receive-pack so they check protection directly.
- `Git/ReleaseService.cs` — releases + annotated tags + assets on `{RepositoryRoot}/_assets/...`.
- `Git/WebhookService.cs` — repo webhooks, HMAC-SHA256 `X-GitSrv-Signature-256`, `hook_deliveries` log.
- PR merge additionally gates on `branch_protections.required_approvals` for the base branch.
- Endpoints: `Endpoints/RepoAdvancedEndpoints.cs`.

## GitSrv Actions (Phase 7)

- `Actions/WorkflowParser.cs` (YamlDotNet) — GitHub-schema subset. `Actions/ActionsService.cs` —
  dispatch (reads `.gitsrv/workflows/*.yml` from the head tree), matrix expansion, run/job/step
  rows, the runner claim contract, log append, step/job/run completion → `ChecksService.SetAsync`.
- Dispatch is triggered from `InternalHookEndpoints` post-receive (push, and pull_request for open
  PRs on the pushed branch) and `PullRequestService.CreateAsync` (pull_request on open).
- `runner/` container: `run.sh` polls `/internal/runner/claim`, clones via
  `http://x-internal:<InternalToken>@api:8080/...` (GitAuthResolver honours that as a trusted
  read), runs steps in a scratch container that shares the runner's `/actions` volume
  (`--volumes-from`), does `${{ matrix.* }}` / `${{ secrets.* }}` / `${{ github.* }}` substitution,
  masks secret values in logs. DooD via the mounted host socket — step containers get the socket
  too (Phase 11 hardening item).
- `Actions/SecretsService.cs` — AES-GCM under `GitSrv:SecretsKey` (falls back to the JWT key).
- `Actions/ChecksService.cs` + `commit_statuses`; `PullRequestService.MergeAsync` gates on
  `branch_protections.require_status_checks` (all statuses on head sha must be `success`).
- Endpoints: `Endpoints/ActionsEndpoints.cs` (+ `RunnerEndpoints`).

## Package registry (Phase 8)

- `Packages/ArtifactStore.cs` — `IArtifactStore` + `LocalArtifactStore` (content under
  `{ArtifactRoot}` = `{RepositoryRoot}/_packages`). `Packages/PackageService.cs` — shared
  `packages`/`package_versions`/`package_files` model, registry auth (PAT bearer or Basic),
  visibility gate.
- `Endpoints/NpmRegistryEndpoints.cs` (`/npm/{org}/…`), `Endpoints/OciRegistryEndpoints.cs`
  (`/v2/…`, `{name}` = `{org}/{image}`, `oci_tags` + `oci_uploads`), `Endpoints/PackageEndpoints.cs`
  (`/generic/…` + `/api/orgs/{slug}/packages` browse). nginx proxies `~ ^/(v2|npm|generic)`
  with `Host $http_host` (npm tarball URLs need the port).
- Web: `views/packages.js`, Packages tab in `orgNav`.

## Enklr integration (Phase 9)

- `Integrations/EnklrService.cs` — per-org `enklr_connections`, `ENK-\d+` reference discovery,
  `enklr_links` (a link per card×source), outbound push (`POST {base}/api/gitsrv/{refs|events}`,
  `Authorization: Bearer {api_token}`), inbound HMAC verification (`X-Enklr-Signature-256`).
- Wired from `PullRequestService.CreateAsync` / `MergeAsync` (pr_opened / pr_merged) and
  `ActionsService.CompleteJobAsync` (CI verdict → `UpdateStateAsync`).
- `Endpoints/EnklrEndpoints.cs`: `/api/orgs/{slug}/enklr` (admin), `.../enklr/cards/{ref}` (link
  list for Enklr), `/api/integrations/enklr/{connectionId}/events` (inbound).
- The Enklr-side card panel lives in the Enklr codebase, not here. Contract tests stub Enklr with
  an in-process HTTP server reached via `host.docker.internal` (compose `extra_hosts`).
- Web: `views/org-settings.js` (org Settings tab).

## Performance (Phase 10)

- `Git/GitMaintenanceWorker.cs` — `BackgroundService`, every 15 min, incremental repack +
  commit-graph + MIDX on repos where `pushed_at > last_maintained_at`.
- `GitStorage.EnsureAsync` sets pack config (bitmaps, commit-graph, MIDX, allowFilter).
- `GitBackend` gates `upload-pack` with a `SemaphoreSlim` (`GitSrv:MaxConcurrentFetches`,
  default 2×CPU).
- `RepoBrowseEndpoints`: `IMemoryCache` for rendered README (`md:{blobSha}`), `ConditionalHit`
  helper sets ETag + Cache-Control and returns 304.
- `IssueService.ListAsync` fetches labels/assignees for the whole page in two `= ANY(@ids)`
  queries, not per-row.
- Web `app.js` — only auth/dashboard/repo-code eager; the rest via `lazy(path, name)` →
  dynamic `import()`.
- Opt-in load probe: `contract-tests/load.test.mjs` (`GITSRV_LOAD=1`).

## Security & ops (Phase 11)

- `Ops/AuditService.cs` — `audit_events`, org-scoped; `Ops/UrlGuard.EnsureSafe` — SSRF guard
  (loopback/private/link-local/169.254.169.254 blocked; `host.docker.internal` and
  `GitSrv:AllowPrivateWebhookHosts=true` are escape hatches) used by WebhookService + EnklrService.
- `Endpoints/OpsEndpoints.cs` — `GET /metrics` (Prometheus text), `/api/orgs/{slug}/audit`
  (+ `?format=csv`), `/api/admin/*` (site-admin filter). nginx proxies `/metrics`.
- Rate limiter: `"auth"` policy on `POST /api/auth/login` only (30/min per IP — register is NOT
  limited so test suites can bulk-register). `UseForwardedHeaders` for real client IP.
- Registration honoured via `instance_settings.registration_open` (first user always allowed).
- `scripts/backup.sh` / `scripts/restore.sh` (pg_dump -Fc + git-data volume tar; `hostpath()`
  handles Windows git-bash). `deploy/docker-compose.prod.yml` — Caddy TLS overlay.

## Repository import (post-Phase 11, migration 012)

- `POST /api/orgs/{slug}/repos/import` (`OrgEndpoints`, org Member) → `RepoService.CreateImportAsync`
  inserts a repo row with `import_source` + `import_status='pending'`. `UrlGuard.EnsureSafe` runs here.
- `Git/RepoImportWorker.cs` (hosted service, 5s poll) claims one `pending` row `FOR UPDATE SKIP LOCKED`,
  `git clone --bare` it, applies config + `WriteHooks` + commit-graph, sets `completed`/`failed`
  (`import_error`). `git` CLI is in the api image already (shared with `GitMaintenanceWorker`).
- `RepoBrowseService.ResolveAsync` skips `storage.EnsureAsync` while `import_status` is
  pending/importing/failed; `/browse/overview` returns an import-state payload (empty refs) instead of
  opening a RepoReader. Front end: `views/repo.js renderRepoCode` shows progress/failed cards;
  `views/org.js` has the "Import" button.
- Test: `contract-tests/import.test.mjs` (imports a public GitSrv repo via `host.docker.internal`).

## Conventions

- Front end: no framework, no bundler runtime. Feature modules in `web/src/js/features/` talk to
  the server only via `web/src/js/api.js`. Colour/spacing only from `--gs-*` tokens
  (`web/src/css/gs-tokens.css`), ported from Enklr's `--kf-*` system, both themes, system-aware.
- Every API and Git path gets an authorization check (from Phase 1, via `can(user, action, resource)`).
- Anything repo-sized is paginated or streamed.

## QA / validation

Docker Desktop is the QA environment. Validate changes with:

```sh
docker compose up -d --build
cd contract-tests && npm test        # GITSRV_BASE_URL defaults to http://localhost:8080
```

## Remote

`origin` → https://github.com/andrewrigney1975-cpu/gitsrv
