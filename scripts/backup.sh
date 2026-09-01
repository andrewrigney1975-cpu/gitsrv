#!/usr/bin/env bash
# GitSrv backup: PostgreSQL dump + a tar of the git-data volume (bare repos, packages, assets).
# Usage:  scripts/backup.sh [output-dir]     (default: ./backups)
# On Windows/git-bash run with:  MSYS_NO_PATHCONV=1 scripts/backup.sh ...
set -euo pipefail
export MSYS_NO_PATHCONV=1   # no-op on Linux; stops git-bash rewriting the container /backup path
hostpath() { (cd "$1" && (pwd -W 2>/dev/null || pwd)); }   # Windows path on git-bash, else POSIX

OUT="${1:-./backups}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
DIR="${OUT}/gitsrv-${STAMP}"
mkdir -p "$DIR"

echo "==> Postgres dump"
docker compose exec -T db pg_dump -U gitsrv -Fc gitsrv > "${DIR}/db.dump"

echo "==> Repository + package volume"
# Stream the git-data volume (bare repos, _packages, _assets) to a tarball via a throwaway container.
docker run --rm -v gitsrv_git-data:/data -v "$(hostpath "$DIR"):/backup" alpine \
  tar czf /backup/git-data.tar.gz -C /data .

echo "==> Manifest"
docker compose exec -T db psql -U gitsrv -tAc "SELECT schema_phase FROM instance_info" | tr -d '[:space:]' > "${DIR}/schema_phase"
cat > "${DIR}/README" <<EOF
GitSrv backup ${STAMP}
  db.dump         pg_dump -Fc of the gitsrv database
  git-data.tar.gz tar of the git-data volume (bare repos, _packages, _assets)
  schema_phase    the migration phase this backup was taken at
Restore with scripts/restore.sh ${DIR}
EOF

echo "Backup written to ${DIR}"
