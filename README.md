# GitSrv

A self-hosted, multi-tenant Git platform — organisations, teams, unlimited repositories, full Git
over HTTP and SSH, pull requests with three-way compare, branch visualisation, commit history and
blame. Lightweight enough to run on a Raspberry Pi 3.

**Stack:** PostgreSQL 16 · ASP.NET Core / C# API · no-framework HTML5/CSS3/ES-module front end ·
each tier in its own container. Visual identity inherited from [Enklr.app](https://enklr.app).

See [`docs/PLAN.md`](docs/PLAN.md) for the twelve-phase build plan.

## Status

**Phase 0 — foundations & skeleton.** Four-container stack, SQL migration runner, design-token
port, CI. The front end talks to the API talks to Postgres; `docker compose up` gives you a live
status page.

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
