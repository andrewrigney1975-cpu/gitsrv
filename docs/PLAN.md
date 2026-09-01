# GitSrv build plan

**Status: all 12 phases complete** (2026-09-01). Each shipped as one commit on `main` with
contract + unit tests green; see `CHANGELOG.md`. This document is the original plan, kept for
reference.

Rendered plan: <https://claude.ai/code/artifact/37464a5f-1917-4f40-9d5d-2ef18524c754>

Twelve phases. Each ends at a demoable, deployed state. Critical path is **0 → 1 → 2 → 3 → 4** —
nothing else matters until pull requests work end to end.

## Architecture (locked in Phase 0)

- **db** — PostgreSQL 16. Relational metadata only; never Git object storage. Migrations are
  ordered SQL files applied on API start-up.
- **api** — ASP.NET Core (C#), minimal APIs. Git plumbing via `git` child processes; reads via
  libgit2 from Phase 3. Smart-HTTP and SSH transports. JWT sessions, SSH public-key auth.
- **web** — nginx serving no-framework HTML5/CSS3/ES modules. Enklr `--gs-*` token system, both
  themes.
- **git storage** — bare repos on a shared volume at `{root}/{org-slug}/{repo-slug}.git`, shared
  by the api and ssh containers.

Cross-cutting from Phase 1, checked every phase: authorization on every path; structured logging +
metrics; pagination/streaming for anything repo-sized; slugs validated and immutable-with-redirect;
a Git-transport contract-test suite.

## Phases

| # | Phase | Done when |
|---|-------|-----------|
| 0 | Foundations & skeleton | `compose up` → page loads, `/health` green on all tiers |
| 1 | Identity: orgs, users, teams, sessions | user creates org, invites member, forms team, creates empty repo record |
| 2 | Git transport: push & pull (HTTP + SSH) | clone/commit/push/pull over both transports, permissions enforced |
| 3 | Repository browsing (tree, file, history, blame, branch graph) | browse/read/history/blame/graph, <200 ms warm on a mid-size repo |
| 4 | Pull requests & three-way compare | open PR → inline review → resolve threads → squash-merge → branch auto-deletes |
| 5 | Issues, collaboration & notifications | file issue → get mentioned → email + inbox → close from a PR |
| 6 | Advanced Git ops & branch policy | protected `main` rejects direct push; web cherry-pick + tagged release succeed |
| 7 | GitSrv Actions (CI/CD) | push runs a matrix build, status check posts back, PR merge unlocks on green |
| 8 | Artifact / package registry | `npm publish` and `docker push` to an org succeed and install back |
| 9 | Enklr.app project-management integration | PR mentioning a card updates it; card shows live PR status |
| 10 | Performance & scale hardening | latency + memory budgets met under load test on a Pi 3 and a 1 GB/4-core target |
| 11 | Security, operability & release | clean-host install from published images passes smoke suite; restore drill passes |

## Sequencing

- Critical path: 0 → 1 → 2 → 3 → 4.
- After 4: 5 and 6 in parallel.
- After 6: 7 and 8 (8 only needs 2 and can start earlier with capacity).
- 9 once PR + issue models are stable.
- 10 and 11 are continuous; the numbered phases are the final hardening sweep.

## Principal risks

1. SSH transport auth (`AuthorizedKeysCommand` + forced-command routing) — spike in Phase 1.
2. Three-way merge preview + conflict rendering (Phase 4) — hardest single feature; lean on
   `git merge-tree`.
3. Actions compatibility (Phase 7) — ship a documented subset, don't chase full parity.
4. No-framework front end discipline — fixed module vocabulary from Phase 0, held to.
