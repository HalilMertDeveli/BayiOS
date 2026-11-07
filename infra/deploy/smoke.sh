#!/usr/bin/env bash
# Post-deploy smoke check (issue #70, deploy-and-rollback.md §3 step 5). Runnable locally:
#
#   API_URL=https://bayios-api-preview.fly.dev EXPECT_ENVIRONMENT=Preview bash infra/deploy/smoke.sh
#
#   GET /health          = 200 (liveness)
#   GET /health/ready    = EXPECT_READY_STATUS (default 200: database + auth configuration reachable)
#   GET /api/v1/meta     -> "environment" = EXPECT_ENVIRONMENT
# Retries until SMOKE_TIMEOUT seconds (default 180) because a Fly machine may be cold-starting (auto-stop).
set -euo pipefail

: "${API_URL:?API_URL is required}" "${EXPECT_ENVIRONMENT:?EXPECT_ENVIRONMENT is required}"
EXPECT_READY_STATUS="${EXPECT_READY_STATUS:-200}"
SMOKE_TIMEOUT="${SMOKE_TIMEOUT:-180}"
base="${API_URL%/}"
deadline=$(( $(date +%s) + SMOKE_TIMEOUT ))

status_of() { curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$1" || true; }

wait_for() { # $1 path, $2 expected status
  local code=""
  while :; do
    code="$(status_of "${base}$1")"
    [ "$code" = "$2" ] && { echo "ok: GET $1 -> $code"; return 0; }
    if [ "$(date +%s)" -ge "$deadline" ]; then
      echo "::error::smoke: GET $1 returned '${code}', expected $2 (after ${SMOKE_TIMEOUT}s)"
      return 1
    fi
    sleep 5
  done
}

wait_for /health 200
wait_for /health/ready "$EXPECT_READY_STATUS"

meta="$(curl -fsS --max-time 10 "${base}/api/v1/meta")"
env_name="$(printf '%s' "$meta" | grep -oE '"environment" *: *"[^"]*"' | sed -E 's/.*"([^"]*)"$/\1/')"
[ "$env_name" = "$EXPECT_ENVIRONMENT" ] \
  || { echo "::error::smoke: /api/v1/meta environment is '${env_name}', expected '${EXPECT_ENVIRONMENT}'"; exit 1; }
echo "ok: /api/v1/meta environment = ${env_name} (${meta})"
