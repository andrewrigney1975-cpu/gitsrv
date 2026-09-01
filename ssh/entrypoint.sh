#!/bin/sh
set -eu

# sshd runs AuthorizedKeysCommand and the forced command with a minimal environment, so the API
# location and internal token are written to a file the scripts read instead of being inherited.
mkdir -p /etc/gitsrv
cat > /etc/gitsrv/env <<EOF
GITSRV_API_BASE=${GITSRV_API_BASE:-http://api:8080}
GITSRV_INTERNAL_TOKEN=${GITSRV_INTERNAL_TOKEN:-}
EOF
chmod 0644 /etc/gitsrv/env

# Generate host keys on first boot (persisted only for the container's lifetime — fine for a
# single-host dev/QA stack; a real deployment should mount /etc/ssh from a volume).
[ -f /etc/ssh/ssh_host_ed25519_key ] || ssh-keygen -q -t ed25519 -N '' -f /etc/ssh/ssh_host_ed25519_key
[ -f /etc/ssh/ssh_host_rsa_key ]     || ssh-keygen -q -t rsa -b 4096 -N '' -f /etc/ssh/ssh_host_rsa_key

exec /usr/sbin/sshd -D -e
