# Phase 0 stub. Builds, runs, and answers a health probe so the four-tier compose stack is
# complete and CI exercises the image. Phase 2 replaces this with a real sshd whose
# AuthorizedKeysCommand calls the API and whose forced command routes git-upload-pack /
# git-receive-pack through an authorisation shim.
FROM alpine:3.20
RUN apk add --no-cache openssh-server git curl
RUN mkdir -p /var/lib/gitsrv/repositories

# Placeholder entrypoint: idle, but responsive to `docker inspect`/healthcheck.
HEALTHCHECK --interval=30s --timeout=3s CMD test -d /var/lib/gitsrv/repositories || exit 1

CMD ["sh", "-c", "echo 'gitsrv ssh tier: Phase 0 stub, no transport yet' && exec sleep infinity"]
