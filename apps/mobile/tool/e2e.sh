#!/usr/bin/env bash
# Web ↔ mobile real-flow test (#45) with one command — real stack, real Android emulator, nothing mocked.
#
#   1. web   apps/web/e2e-local/web-mobile-1-prepare.spec.ts   fresh business + warehouse_staff user (API); the owner
#                                                               creates and approves an order in the web UI
#   2. phone integration_test/smoke_test.dart                   staff signs in / out (smoke)
#            integration_test/web_flow_test.dart                staff: stock + reservation, no "Düzelt", goods receipt,
#                                                               claims the order's task, wrong product / shelf, picks,
#                                                               completes
#   3. web   apps/web/e2e-local/web-mobile-2-verify.spec.ts    owner sees task completed, order packed, stock reserved
#                                                               and received; packs + ships; staff cannot adjust (API)
#
# Usage (repo root or anywhere; Git Bash on Windows):
#   apps/mobile/tool/e2e.sh                         # against a RUNNING stack (API :5280, web :3200, Supabase :54321)
#   E2E_START_STACK=1 E2E_API_PORT=5380 E2E_WEB_PORT=3300 E2E_PG_PORT=55632 apps/mobile/tool/e2e.sh
#                                                   # starts infra/ci/e2e-stack.sh up first (see the warning below)
#
# Env:
#   E2E_API_PORT / E2E_WEB_PORT   ports of the API / web app on this machine (default 5280 / 3200)
#   E2E_DEVICE                    adb device id (default: emulator-5554); start the AVD first, e.g.
#                                 `emulator -avd BayiOS_Smoke_API36`
#   E2E_DEVICE_HOST               how the device reaches this machine (default 10.0.2.2 = emulator → host loopback)
#   SUPABASE_PUBLISHABLE_KEY      public local-dev key; default: apps/web/.env.local, then `supabase status`
#   SUPABASE_WORKDIR              Supabase CLI workdir for that lookup (default infra/supabase/local of this checkout)
#   E2E_START_STACK=1             run `infra/ci/e2e-stack.sh up` first. It (re)starts the SHARED local Supabase on 54321
#                                 and a throw-away Postgres on E2E_PG_PORT; this script never runs `down` (that would
#                                 stop Supabase for every other local stack). Windows: E2E_PG_PORT must not be 55432.
#   E2E_MOBILE_STATE_DIR          per-run state + define files (default apps/mobile/build/e2e, git-ignored). They hold
#                                 the run's synthetic logins: never print or commit them.
#
# Every run creates its own tenant, users and products (unique names) — it never depends on hand-made data and never
# touches existing tenants, so it is safe on a developer's running stack. Screenshots: $E2E_MOBILE_STATE_DIR/screenshots.
set -euo pipefail

MOBILE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "${MOBILE_DIR}/../.." && pwd)"
WEB_DIR="${REPO_ROOT}/apps/web"
API_PORT="${E2E_API_PORT:-5280}"
WEB_PORT="${E2E_WEB_PORT:-3200}"
DEVICE="${E2E_DEVICE:-emulator-5554}"
DEVICE_HOST="${E2E_DEVICE_HOST:-10.0.2.2}"
SUPABASE_URL="http://127.0.0.1:54321"
STATE_DIR="${E2E_MOBILE_STATE_DIR:-${MOBILE_DIR}/build/e2e}"
SHOTS="${STATE_DIR}/screenshots"
ADB="adb"
if ! command -v adb >/dev/null 2>&1 && [ -n "${ANDROID_HOME:-}" ]; then ADB="${ANDROID_HOME}/platform-tools/adb"; fi

log() { printf '\n==> [%s] %s\n' "$(date -u +%H:%M:%S)" "$*"; }
fail() { printf '::error::%s\n' "$*" >&2; exit 1; }
native_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
step_started=$(date +%s)
# Logs the duration of the step that just ended (not in $(...): the subshell would lose the new start time).
done_step() { local now; now=$(date +%s); log "$1 in $((now - step_started))s"; step_started=$now; }

if [ "${E2E_START_STACK:-0}" = 1 ]; then
  log "Starting the real stack (infra/ci/e2e-stack.sh up)"
  E2E_API_PORT="$API_PORT" E2E_WEB_PORT="$WEB_PORT" bash "${REPO_ROOT}/infra/ci/e2e-stack.sh" up
  done_step "stack up"
fi

export E2E_API_URL="http://localhost:${API_PORT}"
export E2E_WEB_URL="http://localhost:${WEB_PORT}"
export E2E_MOBILE_STATE_DIR
E2E_MOBILE_STATE_DIR="$(native_path "$STATE_DIR")"

log "Checking the stack and the device"
curl --silent --fail --max-time 10 -o /dev/null "http://127.0.0.1:${API_PORT}/health/ready" || fail "API not ready on :${API_PORT}"
curl --silent --fail --max-time 30 -o /dev/null "${E2E_WEB_URL}/giris" || fail "web app not reachable on :${WEB_PORT}"
curl --silent --fail --max-time 10 -o /dev/null "${SUPABASE_URL}/auth/v1/health" || fail "Supabase Auth not reachable on :54321"
[ "$("$ADB" -s "$DEVICE" get-state 2>/dev/null || true)" = device ] || fail "adb device ${DEVICE} is not online (start the emulator)"

# Public local-development key of the Supabase CLI stack (still never printed or committed).
if [ -z "${SUPABASE_PUBLISHABLE_KEY:-}" ] && [ -f "${WEB_DIR}/.env.local" ]; then
  SUPABASE_PUBLISHABLE_KEY="$(sed -n 's/^NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY=//p' "${WEB_DIR}/.env.local" | tr -d '"'"'"'\r' | head -n1)"
fi
if [ -z "${SUPABASE_PUBLISHABLE_KEY:-}" ]; then
  workdir="${SUPABASE_WORKDIR:-${REPO_ROOT}/infra/supabase/local}"
  SUPABASE_PUBLISHABLE_KEY="$(npx -y supabase@2.122.1 status -o json --workdir "$(native_path "$workdir")" 2>/dev/null \
    | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{const j=JSON.parse(s.slice(s.indexOf("{")));process.stdout.write(j.PUBLISHABLE_KEY||j.ANON_KEY||"")}catch{}})')"
fi
[ -n "${SUPABASE_PUBLISHABLE_KEY:-}" ] || fail "No Supabase publishable key: set SUPABASE_PUBLISHABLE_KEY or SUPABASE_WORKDIR (see apps/mobile/README.md)"
export SUPABASE_PUBLISHABLE_KEY

mkdir -p "$STATE_DIR" "$SHOTS"
rm -f "${STATE_DIR}/state.json" "${STATE_DIR}/flow.json" "${SHOTS}"/*.png
# The app's own environment (debug build: plain HTTP is allowed to 10.0.2.2 only).
node -e 'require("fs").writeFileSync(process.argv[1], JSON.stringify({
  BAYIOS_ENV: "dev",
  BAYIOS_API_BASE_URL: `http://${process.argv[2]}:${process.argv[3]}`,
  BAYIOS_SUPABASE_URL: `http://${process.argv[2]}:54321`,
  BAYIOS_SUPABASE_PUBLISHABLE_KEY: process.env.SUPABASE_PUBLISHABLE_KEY,
}, null, 2) + "\n", { mode: 0o600 })' "$(native_path "${STATE_DIR}/env.json")" "$DEVICE_HOST" "$API_PORT"

log "1/3 web: fresh business + staff user, order created and approved on the web"
(cd "$WEB_DIR" && npx playwright test -c playwright.local.config.ts web-mobile-1-prepare)
[ -s "${STATE_DIR}/flow.json" ] || fail "phase 1 wrote no ${STATE_DIR}/flow.json"
done_step "phase 1"

cd "$MOBILE_DIR"
export BAYIOS_SCREENSHOT_DIR
BAYIOS_SCREENSHOT_DIR="$(native_path "$SHOTS")"
defines=(--dart-define-from-file="$(native_path "${STATE_DIR}/env.json")" --dart-define-from-file="$(native_path "${STATE_DIR}/flow.json")")

log "2/3 phone (${DEVICE}): sign-in smoke as the staff user"
flutter drive -d "$DEVICE" --driver=test_driver/integration_test.dart --target=integration_test/smoke_test.dart "${defines[@]}"
done_step "smoke"

log "2/3 phone (${DEVICE}): goods receipt + claim + pick of the web order"
flutter drive -d "$DEVICE" --driver=test_driver/integration_test.dart --target=integration_test/web_flow_test.dart "${defines[@]}"
done_step "mobile flow"

log "3/3 web: the owner verifies, packs and ships; staff permission split"
(cd "$WEB_DIR" && npx playwright test -c playwright.local.config.ts web-mobile-2-verify)
done_step "phase 3"

log "Web ↔ mobile flow passed. Screenshots: ${SHOTS}"
