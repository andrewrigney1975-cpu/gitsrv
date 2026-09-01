# GitSrv

A self-hosted, multi-tenant Git platform — organisations, teams, unlimited repositories, full Git
over HTTP and SSH, pull requests with three-way compare, branch visualisation, commit history and
blame. Lightweight enough to run on a Raspberry Pi 3.

**Stack:** PostgreSQL 16 · ASP.NET Core / C# API · no-framework HTML5/CSS3/ES-module front end ·
each tier in its own container. Visual identity inherited from [Enklr.app](https://enklr.app).

See [`docs/PLAN.md`](docs/PLAN.md) for the twelve-phase build plan.

## Status

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
