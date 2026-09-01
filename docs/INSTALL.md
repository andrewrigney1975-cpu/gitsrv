# Installing GitSrv

## Requirements

- Docker + Docker Compose v2
- A host with ~1 GB RAM and 1+ CPU for a small team (runs on a Raspberry Pi 3)
- For CI: the host's Docker socket is mounted into the `runner` container

## Local / evaluation

```sh
git clone https://github.com/andrewrigney1975-cpu/gitsrv
cd gitsrv
cp .env.example .env          # set DB_PASSWORD, JWT_SIGNING_KEY (>=32 chars), INTERNAL_TOKEN
docker compose up --build -d
```

Open `http://localhost:${WEB_PORT:-8080}`. The **first account you register becomes the site
admin**. Mail is captured by the `mail` container — its inbox is at `http://localhost:8025`.

## Production

1. Put real, random values in `.env`:
   - `DB_PASSWORD`, `JWT_SIGNING_KEY` (32+ chars), `INTERNAL_TOKEN`
   - `PUBLIC_BASE_URL=https://git.example.com`
   - `GITSRV_DOMAIN=git.example.com`
   - `ASPNETCORE_ENVIRONMENT=Production`
2. Bring it up with the production overlay (adds a TLS-terminating Caddy proxy, closes host ports):

   ```sh
   docker compose -f docker-compose.yml -f deploy/docker-compose.prod.yml up -d
   ```

   The API refuses to start outside Development if it still sees the checked-in placeholder
   secrets, so a misconfigured deploy fails fast.

3. SSH git access is published on `${SSH_PORT:-2222}`. Point users at
   `git@git.example.com:org/repo.git` (adjust the port, or map 22 on the host).

## Backups

```sh
scripts/backup.sh /var/backups/gitsrv      # pg_dump + git-data volume tar
scripts/restore.sh /var/backups/gitsrv/gitsrv-<stamp>
```

Run a restore drill on a spare host before you need one.

## Health & metrics

- `GET /health` — API + DB liveness (used by the compose healthcheck)
- `GET /metrics` — Prometheus text format (users, repos, open PRs, queued CI jobs, repo bytes,
  working set). Keep it behind the proxy or an allowlist in production.
- Structured JSON logs to stdout: `docker compose logs -f api`
