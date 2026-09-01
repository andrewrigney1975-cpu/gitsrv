# db tier

Stock `postgres:16-alpine` (see `docker-compose.yml`) — no custom image. This directory holds
database-adjacent notes and, later, any init scripts or tuning config.

## Migrations

Schema changes are **not** managed here. They live with the API as ordered SQL files in
`api/GitSrv.Api/Migrations/Sql/` and are applied on API start-up by `MigrationRunner`:

- Forward-only. No down-migrations — roll forward with a new file.
- Filename order is apply order: `001_init.sql`, `002_identity.sql`, …
- Each file is applied once, in a transaction, and recorded in `schema_migrations` with a
  checksum. Editing an already-applied file is rejected at start-up.

## Data

PostgreSQL stores **only relational metadata** — users, orgs, repos, PRs, issues, etc. Git object
storage is bare repositories on the `git-data` volume, never the database.
