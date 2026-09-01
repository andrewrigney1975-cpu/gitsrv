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
