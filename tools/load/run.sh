#!/usr/bin/env bash
# Runs a k6 scenario against the load stack (tools/load/stack.sh up). k6 runs in its official image, pinned by digest.
#
#   bash tools/load/run.sh mix     # STEPS="5,10,20,40" STEP_SECONDS=60 (stepped open-model mix)
#   bash tools/load/run.sh soak    # ITERATIONS=200 RACERS=8 (reservation race soak)
#   bash tools/load/run.sh ci      # short CI profile: soak 25 iterations + mix at 5 and 10 it/s for 20 s each
#
# Results: $LOAD_STATE_DIR/<scenario>-summary.json and the step table on stdout.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
STATE_DIR="${LOAD_STATE_DIR:-${RUNNER_TEMP:-${TMPDIR:-/tmp}}/bayios-load}"
API_PORT="${LOAD_API_PORT:-5390}"
K6_IMAGE="${K6_IMAGE:-grafana/k6:1.4.2@sha256:3656673de3f30424e8ebcfa46acd9558d83b6a43612d0f668ffeac953950c6c7}"
native_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

# Linux: host network, the API listens on 127.0.0.1. Docker Desktop (Windows/macOS): host.docker.internal.
if [ "$(uname -s)" = Linux ]; then
  network=(--network host); base_url="http://127.0.0.1:${API_PORT}"
else
  network=(); base_url="http://host.docker.internal:${API_PORT}"
fi

k6() { # script, env...
  local script="$1"; shift
  local env_args=()
  for kv in "$@"; do env_args+=(-e "$kv"); done
  MSYS_NO_PATHCONV=1 docker run --rm "${network[@]}" \
    -v "$(native_path "${REPO_ROOT}/tools/load"):/scripts:ro" \
    -v "$(native_path "$STATE_DIR"):/state" \
    -e BASE_URL="$base_url" -e LOAD_DEBUG="${LOAD_DEBUG:-}" "${env_args[@]}" \
    "$K6_IMAGE" run --quiet "/scripts/${script}"
}

[ -s "${STATE_DIR}/tokens.json" ] || { echo "No tokens: run 'bash tools/load/stack.sh up' first." >&2; exit 1; }
case "${1:-}" in
  mix) k6 mix.js STEPS="${STEPS:-5,10,20,40}" STEP_SECONDS="${STEP_SECONDS:-60}" RAMP_SECONDS="${RAMP_SECONDS:-10}" PRODUCTS="${PRODUCTS:-300}" SUMMARY="${SUMMARY:-/state/mix-summary.json}" ;;
  soak) k6 soak.js ITERATIONS="${ITERATIONS:-200}" RACERS="${RACERS:-8}" SUMMARY="${SUMMARY:-/state/soak-summary.json}" ;;
  ci)
    k6 soak.js ITERATIONS="${ITERATIONS:-25}" RACERS="${RACERS:-8}" SUMMARY=/state/ci-soak-summary.json
    k6 mix.js STEPS="5,10" STEP_SECONDS=20 RAMP_SECONDS=5 PRODUCTS=60 SUMMARY=/state/ci-mix-summary.json
    ;;
  *) echo "usage: $0 mix|soak|ci" >&2; exit 2 ;;
esac
