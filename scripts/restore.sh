#!/usr/bin/env bash
# GitSrv restore. Stops the app, restores the DB and the git-data volume, restarts.
# Usage:  scripts/restore.sh <backup-dir>
set -euo pipefail
export MSYS_NO_PATHCONV=1
hostpath() { (cd "$1" && (pwd -W 2>/dev/null || pwd)); }

DIR="${1:?usage: restore.sh <backup-dir>}"
[ -f "${DIR}/db.dump" ] || { echo "no db.dump in ${DIR}"; exit 1; }
[ -f "${DIR}/git-data.tar.gz" ] || { echo "no git-data.tar.gz in ${DIR}"; exit 1; }

read -rp "This will OVERWRITE the current database and repositories. Continue? [y/N] " ok
[ "$ok" = "y" ] || exit 1

echo "==> Stopping app tiers"
docker compose stop api web ssh runner

echo "==> Restoring Postgres"
docker compose exec -T db psql -U gitsrv -d postgres -c "DROP DATABASE IF EXISTS gitsrv;" -c "CREATE DATABASE gitsrv OWNER gitsrv;"
docker compose exec -T db pg_restore -U gitsrv -d gitsrv --no-owner < "${DIR}/db.dump"

echo "==> Restoring git-data volume"
docker run --rm -v gitsrv_git-data:/data -v "$(hostpath "$DIR"):/backup" alpine \
  sh -c 'rm -rf /data/* /data/..?* /data/.[!.]* 2>/dev/null; tar xzf /backup/git-data.tar.gz -C /data'

echo "==> Starting app tiers"
docker compose up -d

echo "Restore complete. Verify with:  cd contract-tests && npm test"
