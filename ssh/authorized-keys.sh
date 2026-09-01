#!/bin/sh
# sshd AuthorizedKeysCommand. Argument: the offered key's SHA256 fingerprint (%f), e.g.
# "SHA256:abc...". Prints authorized_keys line(s) for it, or nothing (exit 0) if unknown.
set -eu
. /etc/gitsrv/env

FINGERPRINT="${1:-}"
[ -n "$FINGERPRINT" ] || exit 0

curl -sf -G "${GITSRV_API_BASE}/internal/ssh/authorized-keys" \
  -H "X-Internal-Token: ${GITSRV_INTERNAL_TOKEN}" \
  --data-urlencode "fingerprint=${FINGERPRINT}" || exit 0
