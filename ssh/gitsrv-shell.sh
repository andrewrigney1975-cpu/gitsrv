#!/bin/sh
# Forced command for every GitSrv ssh session (pinned in the authorized_keys line the API returns).
# Re-checks authorization with the API, then exec's the pack program against the resolved path.
# Never trusts $SSH_ORIGINAL_COMMAND for anything but the verb and the repo path string.
set -eu
. /etc/gitsrv/env

KEY_ID=""
case "${1:-}" in
  --key-id=*) KEY_ID="${1#--key-id=}" ;;
esac
[ -n "$KEY_ID" ] || { echo "GitSrv: malformed session." >&2; exit 1; }

CMD="${SSH_ORIGINAL_COMMAND:-}"
[ -n "$CMD" ] || { echo "GitSrv: this account only serves git; interactive shells are disabled." >&2; exit 1; }

VERB="${CMD%% *}"
case "$VERB" in
  git-upload-pack|git-receive-pack) ;;
  *) echo "GitSrv: unsupported command '$VERB'." >&2; exit 1 ;;
esac

ARG="${CMD#* }"
ARG="${ARG#\'}"; ARG="${ARG%\'}"
ARG="${ARG#\"}"; ARG="${ARG%\"}"
ARG="${ARG#/}"; ARG="${ARG#\~/}"

RESP="$(curl -sf -X POST "${GITSRV_API_BASE}/internal/ssh/authorize" \
  -H "X-Internal-Token: ${GITSRV_INTERNAL_TOKEN}" -H 'Content-Type: application/json' \
  -d "{\"keyId\":${KEY_ID},\"operation\":\"${VERB}\",\"repoPath\":\"${ARG}\"}")" \
  || { echo "GitSrv: access denied." >&2; exit 1; }

if [ "$(printf '%s' "$RESP" | jq -r '.allowed')" != "true" ]; then
  echo "GitSrv: $(printf '%s' "$RESP" | jq -r '.reason // "access denied"')" >&2
  exit 1
fi

DIR="$(printf '%s' "$RESP" | jq -r '.absolutePath')"
REPO_ID="$(printf '%s' "$RESP" | jq -r '.repoId')"
ORG_ID="$(printf '%s' "$RESP" | jq -r '.orgId')"

if [ "$VERB" = "git-receive-pack" ]; then
  "$VERB" "$DIR"
  status=$?
  curl -sf -X POST "${GITSRV_API_BASE}/internal/ssh/pushed/${REPO_ID}/${ORG_ID}" \
    -H "X-Internal-Token: ${GITSRV_INTERNAL_TOKEN}" >/dev/null 2>&1 || true
  exit $status
fi

exec "$VERB" "$DIR"
