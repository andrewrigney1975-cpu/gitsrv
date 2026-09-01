# Upgrading GitSrv

GitSrv migrations are **forward-only** ordered SQL files in
`api/GitSrv.Api/Migrations/Sql/` (`001_init.sql` … `011_ops.sql`). The API applies every pending
one on start-up, in a transaction, and records a checksum — editing an applied migration file
fails start-up, so always add a new file.

## Procedure

```sh
scripts/backup.sh                     # always back up first
git pull
docker compose pull                   # or: docker compose build
docker compose up -d
docker compose logs api | grep -i migration   # confirm the new files applied
cd contract-tests && npm test         # smoke the upgraded stack
```

Downtime is a few seconds while `api` restarts. `web`, `ssh` and `runner` restart independently.

## Rolling back

There are no down-migrations. To roll back a bad upgrade, restore the pre-upgrade backup
(`scripts/restore.sh`) and pin the previous image tag.

## Version / phase

`GET /api/meta` reports `{ "name": "GitSrv", "phase": N, "version": "..." }`. The `schema_phase`
row in `instance_info` is the DB's migration level; a backup records it in `schema_phase`.
