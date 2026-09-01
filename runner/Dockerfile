# GitSrv Actions runner. Polls the API for queued jobs and executes each job's steps inside a
# throwaway container (Docker-out-of-Docker via the mounted host socket). A single-replica poller;
# scale with `docker compose up --scale runner=N`.
FROM docker:27-cli
RUN apk add --no-cache bash curl jq git coreutils
COPY runner/run.sh /usr/local/bin/gitsrv-runner
RUN chmod 0755 /usr/local/bin/gitsrv-runner
ENTRYPOINT ["/usr/local/bin/gitsrv-runner"]
