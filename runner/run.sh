#!/bin/bash
# GitSrv Actions runner. Claims one job at a time from the API, runs its steps in a scratch
# container, streams logs back, and reports the conclusion. Not a full GitHub Actions engine —
# `run:` steps, `actions/checkout` (implicit), matrix expansion, and repo/org secrets.
set -u

API="${GITSRV_API_BASE:-http://api:8080}"
TOKEN="${GITSRV_INTERNAL_TOKEN:?GITSRV_INTERNAL_TOKEN required}"
RUNNER_ID="${GITSRV_RUNNER_ID:-runner-1}"
DEFAULT_IMAGE="${GITSRV_DEFAULT_IMAGE:-ubuntu:24.04}"
SELF="$(hostname)"          # this container's id — step containers mount our volumes via --volumes-from
WORK_ROOT="/actions"        # named volume shared with step containers
IH=(-H "X-Internal-Token: ${TOKEN}")

log() { printf '[runner] %s\n' "$*" >&2; }

# ---- helpers -------------------------------------------------------------

image_for() {
  case "$1" in
    ubuntu-latest|ubuntu-22.04|ubuntu-24.04|ubuntu-20.04) echo "$DEFAULT_IMAGE" ;;
    *) echo "$1" ;;   # treat anything else as an explicit image name
  esac
}

# stdin -> API as a JSON array of lines, secret values masked
push_logs() {
  local job=$1 tok=$2 step=$3
  local raw; raw="$(cat)"
  local masked="$raw"
  for v in "${SECRET_VALUES[@]}"; do
    [ -n "$v" ] && masked="${masked//$v/***}"
  done
  local arr; arr="$(jq -Rs 'rtrimstr("\n") | split("\n")' <<<"$masked")"
  curl -s "${IH[@]}" -X POST "${API}/internal/runner/jobs/${job}/logs?token=${tok}" \
    -H 'Content-Type: application/json' -d "{\"stepNumber\":${step},\"lines\":${arr}}" >/dev/null
}

step_status() {  # job token n status [conclusion] [exit]
  local body="{\"status\":\"$4\""
  [ -n "${5:-}" ] && body="${body},\"conclusion\":\"$5\""
  [ -n "${6:-}" ] && body="${body},\"exitCode\":$6"
  body="${body}}"
  curl -s "${IH[@]}" -X POST "${API}/internal/runner/jobs/$1/steps/$3?token=$2" \
    -H 'Content-Type: application/json' -d "$body" >/dev/null
}

# ---- job execution -----------------------------------------------------

run_job() {
  local job="$1"
  local jobId token cloneUrl sha runsOn image cid wd
  jobId=$(jq -r '.jobId' <<<"$job")
  token=$(jq -r '.jobToken' <<<"$job")
  cloneUrl=$(jq -r '.cloneUrl' <<<"$job")
  sha=$(jq -r '.headSha' <<<"$job")
  runsOn=$(jq -r '.runsOn' <<<"$job")
  image="$(image_for "$runsOn")"

  # secret values (for masking) and container env
  mapfile -t SECRET_VALUES < <(jq -r '(.secrets // {}) | .[]' <<<"$job")
  local -a cenv=()
  while IFS= read -r kv; do cenv+=(-e "$kv"); done < <(jq -r '
     ((.secrets // {}) | to_entries[] | "\(.key)=\(.value)"),
     ((.matrix  // {}) | to_entries[] | "\(.key)=\(.value)"),
     ((.github  // {}) | to_entries[] | "GITHUB_\(.key | ascii_upcase)=\(.value)"),
     ((.env     // {}) | to_entries[] | "\(.key)=\(.value)")' <<<"$job")

  wd="${WORK_ROOT}/${jobId}-$$"
  mkdir -p "$wd"
  cid="gsrun-${jobId}-$$"
  docker rm -f "$cid" >/dev/null 2>&1 || true

  log "job ${jobId}: ${image}, ${sha:0:7}"
  if ! git clone -q "$cloneUrl" "$wd/repo" 2>"$wd/clone.err"; then
    step_status "$jobId" "$token" 1 completed failure 1
    push_logs "$jobId" "$token" 1 < "$wd/clone.err"
    curl -s "${IH[@]}" -X POST "${API}/internal/runner/jobs/${jobId}/complete?token=${token}" \
      -H 'Content-Type: application/json' -d '{"conclusion":"failure"}' >/dev/null
    rm -rf "$wd"; return
  fi
  git -C "$wd/repo" -c advice.detachedHead=false checkout -q "$sha"

  docker run -d --name "$cid" --volumes-from "$SELF" -w "$wd/repo" "${cenv[@]}" "$image" sleep 7200 >/dev/null

  local conclusion="success" nsteps i
  nsteps=$(jq '.steps | length' <<<"$job")
  for ((i=0; i<nsteps; i++)); do
    local step n kind script coe ec=0
    step=$(jq -c ".steps[$i]" <<<"$job")
    n=$(jq -r '.number' <<<"$step")
    kind=$(jq -r '.kind' <<<"$step")
    coe=$(jq -r '.continueOnError' <<<"$step")
    step_status "$jobId" "$token" "$n" running

    if [ "$kind" = "checkout" ]; then
      printf 'Checked out %s\n' "$sha" | push_logs "$jobId" "$token" "$n"
    elif [ "$kind" = "uses" ]; then
      printf 'GitSrv Actions does not run "uses:" steps yet — skipped: %s\n' "$(jq -r '.uses' <<<"$step")" | push_logs "$jobId" "$token" "$n"
    else
      script=$(jq -r '.run' <<<"$step")
      # ${{ matrix.X }} -> value ; ${{ secrets.Y }} -> $Y ; ${{ github.Z }} -> $GITHUB_Z
      while IFS=$'\t' read -r k v; do
        script="${script//\$\{\{ matrix.${k} \}\}/$v}"
        script="${script//\$\{\{matrix.${k}\}\}/$v}"
      done < <(jq -r '(.matrix // {}) | to_entries[] | "\(.key)\t\(.value)"' <<<"$job")
      script="$(sed -E 's/\$\{\{ *secrets\.([A-Za-z_][A-Za-z0-9_]*) *\}\}/${\1}/g; s/\$\{\{ *github\.([A-Za-z_]+) *\}\}/${GITHUB_\U\1}/g' <<<"$script")"

      local -a senv=()
      while IFS= read -r kv; do senv+=(-e "$kv"); done < <(jq -r '(.env // {}) | to_entries[] | "\(.key)=\(.value)"' <<<"$step")

      docker exec "${senv[@]}" "$cid" bash -eo pipefail -c "$script" >"$wd/step.out" 2>&1
      ec=$?
      push_logs "$jobId" "$token" "$n" < "$wd/step.out"
    fi

    if [ "$ec" -ne 0 ]; then
      step_status "$jobId" "$token" "$n" completed failure "$ec"
      if [ "$coe" != "true" ]; then conclusion="failure"; break; fi
    else
      step_status "$jobId" "$token" "$n" completed success "$ec"
    fi
  done

  docker rm -f "$cid" >/dev/null 2>&1 || true
  rm -rf "$wd"
  curl -s "${IH[@]}" -X POST "${API}/internal/runner/jobs/${jobId}/complete?token=${token}" \
    -H 'Content-Type: application/json' -d "{\"conclusion\":\"${conclusion}\"}" >/dev/null
  log "job ${jobId} -> ${conclusion}"
}

# ---- poll loop -------------------------------------------------------

log "polling ${API} as ${RUNNER_ID}"
while true; do
  resp=$(curl -s -o /tmp/claim.json -w '%{http_code}' "${IH[@]}" -X POST "${API}/internal/runner/claim?runnerId=${RUNNER_ID}")
  if [ "$resp" = "200" ] && [ -s /tmp/claim.json ]; then
    run_job "$(cat /tmp/claim.json)"
  else
    sleep 3
  fi
done
