# Git-over-SSH transport. One OS user, `git`; every connection authenticates by public key via
# sshd's AuthorizedKeysCommand (which asks the API whose key it is) and is pinned to a forced
# command (gitsrv-shell) that re-checks authorization with the API before exec'ing the pack program.
FROM alpine:3.20

RUN apk add --no-cache openssh-server git curl jq

# uid 1654 matches the api container's "app" user so both can read/write the shared repo volume.
# The '*' in shadow (rather than adduser -D's '!') is a passwordless-but-not-locked account —
# sshd refuses pubkey auth for a "locked" account.
RUN adduser -D -u 1654 -s /bin/sh git \
    && sed -i 's/^git:!:/git:*:/' /etc/shadow \
    && mkdir -p /var/lib/gitsrv/repositories \
    && chown -R git:git /var/lib/gitsrv

COPY ssh/sshd_config /etc/ssh/sshd_config
COPY ssh/authorized-keys.sh /usr/local/bin/gitsrv-authorized-keys
COPY ssh/gitsrv-shell.sh /usr/local/bin/gitsrv-shell
COPY ssh/entrypoint.sh /usr/local/bin/gitsrv-entrypoint
RUN chmod 0755 /usr/local/bin/gitsrv-authorized-keys /usr/local/bin/gitsrv-shell /usr/local/bin/gitsrv-entrypoint

EXPOSE 22
HEALTHCHECK --interval=15s --timeout=3s CMD pgrep sshd >/dev/null || exit 1
ENTRYPOINT ["/usr/local/bin/gitsrv-entrypoint"]
