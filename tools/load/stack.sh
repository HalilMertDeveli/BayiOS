#!/usr/bin/env bash
# Isolated load-test stack (#58). Runnable from the repo root (bash, Docker, .NET SDK, Node 24):
#
#   bash tools/load/stack.sh up        # Postgres 17 (own container/port) + EF migrations + API (Release, Production)
#   bash tools/load/run.sh mix         # or: soak | ci   (k6 in Docker, see docs/testing/load-2026-11.md)
#   bash tools/load/stack.sh verify    # ledger = balances, no oversell (SQL, read-only)
#   bash tools/load/stack.sh down
#
# Isolation: its own container (bayios-load-postgres) and ports (default PG 55642, API 5390, auth 5391). It never
# starts, stops or reads the developer stack (web 3200, API 5280, Supabase 54321) or the E2E stack (55532/5380/3300).
# Auth: tools/load/auth.mjs — a throwaway in-memory ES256 key and a local JWKS; the API validates tokens exactly as in
# production (asymmetric only, issuer/audience/lifetime). No secrets: passwords are generated per run.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
STATE_DIR="${LOAD_STATE_DIR:-${RUNNER_TEMP:-${TMPDIR:-/tmp}}/bayios-load}"
PG_IMAGE="${PG_IMAGE:-postgres:17}"
PG_CONTAINER="${LOAD_PG_CONTAINER:-bayios-load-postgres}"
PG_PORT="${LOAD_PG_PORT:-55642}"
API_PORT="${LOAD_API_PORT:-5390}"
AUTH_PORT="${LOAD_AUTH_PORT:-5391}"
# Npgsql pool of the API (default 100). Kept explicit so the report states what was measured.
DB_POOL="${LOAD_DB_POOL:-100}"

log() { printf '\n==> [%s] %s\n' "$(date -u +%H:%M:%S)" "$*"; }
fail() { printf '::error::%s\n' "$*" >&2; exit 1; }
native_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
random_password() { od -An -N18 -tx1 /dev/urandom | tr -d ' \n'; }

for port in "$PG_PORT" "$API_PORT" "$AUTH_PORT"; do
  case "$port" in 3200|5280|54321|54322|55532|5380|3300) fail "port ${port} belongs to the developer or E2E stack; pick another" ;; esac
done

wait_http() { # url, seconds
  for _ in $(seq 1 "$2"); do
    if curl --silent --fail --max-time 5 -o /dev/null "$1"; then return 0; fi
    sleep 1
  done
  return 1
}

start_bg() { # name, log file, command...
  local name="$1" logfile="$2"; shift 2
  nohup "$@" >"$logfile" 2>&1 &
  echo $! >"${STATE_DIR}/${name}.pid"
}

stop_bg() {
  local pidfile="${STATE_DIR}/$1.pid"
  [ -f "$pidfile" ] || return 0
  kill "$(cat "$pidfile")" >/dev/null 2>&1 || true
  rm -f "$pidfile"
}

psql_super() { MSYS_NO_PATHCONV=1 docker exec -i "$PG_CONTAINER" psql -U postgres -d bayios -v ON_ERROR_STOP=1 "$@"; }

up() {
  mkdir -p "$STATE_DIR"
  local app_password migrator_password scripts="${STATE_DIR}/migrations"
  app_password="$(random_password)"
  migrator_password="$(random_password)"

  if [ "${LOAD_REUSE_MIGRATIONS:-0}" = 1 ] && ls "${scripts}"/*.sql >/dev/null 2>&1; then
    log "Reusing migration scripts in ${scripts}"
  else
    log "Generating idempotent EF migration scripts (infra/ci/ef-migrations.sh)"
    OUT_DIR="$scripts" PG_CONTAINER="${PG_CONTAINER}-check" bash "${REPO_ROOT}/infra/ci/ef-migrations.sh"
  fi

  log "Starting ${PG_IMAGE} as ${PG_CONTAINER} on 127.0.0.1:${PG_PORT}"
  docker rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true
  MSYS_NO_PATHCONV=1 docker run -d --name "$PG_CONTAINER" -p "127.0.0.1:${PG_PORT}:5432" \
    -e POSTGRES_DB=bayios -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD="$(random_password)" \
    -e BAYIOS_APP_PASSWORD="$app_password" -e BAYIOS_MIGRATOR_PASSWORD="$migrator_password" \
    -v "$(native_path "${REPO_ROOT}/infra/docker/initdb"):/docker-entrypoint-initdb.d:ro" \
    "$PG_IMAGE" -c max_connections=200 >/dev/null
  local ready=0
  for _ in $(seq 1 60); do
    if docker exec "$PG_CONTAINER" pg_isready -h 127.0.0.1 -U postgres -d bayios >/dev/null 2>&1; then ready=1; break; fi
    sleep 1
  done
  [ "$ready" = 1 ] || { docker logs "$PG_CONTAINER"; fail "postgres did not become ready"; }
  sleep 2 # initdb restarts the server once after the init scripts
  for script in "${scripts}"/*.sql; do
    log "Apply $(basename "$script") as bayios_migrator"
    MSYS_NO_PATHCONV=1 docker exec -i -e PGPASSWORD="$migrator_password" "$PG_CONTAINER" \
      psql -h 127.0.0.1 -U bayios_migrator -d bayios -v ON_ERROR_STOP=1 -q -f - <"$script" >/dev/null
  done

  log "Starting the test identity provider on 127.0.0.1:${AUTH_PORT}"
  stop_bg auth
  start_bg auth "${STATE_DIR}/auth.log" node "${REPO_ROOT}/tools/load/auth.mjs" "$STATE_DIR" "$AUTH_PORT" "${LOAD_USERS:-8}"
  wait_http "http://127.0.0.1:${AUTH_PORT}/auth/v1/.well-known/jwks.json" 30 || { cat "${STATE_DIR}/auth.log"; fail "auth did not start"; }

  local out="${STATE_DIR}/api"
  log "Publishing the API (Release)"
  dotnet publish "${REPO_ROOT}/services/api/src/BayiOS.Api/BayiOS.Api.csproj" -c Release -o "$out" -nologo -v quiet
  rm -f "${out}/appsettings.Development.json"
  log "Starting the API on http://127.0.0.1:${API_PORT} (Production, Npgsql pool ${DB_POOL})"
  stop_bg api
  ASPNETCORE_ENVIRONMENT=Production \
  ASPNETCORE_URLS="http://127.0.0.1:${API_PORT}" \
  Logging__LogLevel__Default=Warning \
  ConnectionStrings__Default="Host=127.0.0.1;Port=${PG_PORT};Database=bayios;Username=bayios_app;Password=${app_password};Maximum Pool Size=${DB_POOL}" \
  Auth__Authority="http://127.0.0.1:${AUTH_PORT}/auth/v1" \
  Auth__Audience=authenticated \
  Auth__RequireHttpsMetadata=false \
    start_bg api "${STATE_DIR}/api.log" dotnet "${out}/BayiOS.Api.dll"
  wait_http "http://127.0.0.1:${API_PORT}/health/ready" 60 || { cat "${STATE_DIR}/api.log"; fail "API did not become ready"; }
  log "Load stack ready: API http://127.0.0.1:${API_PORT} · tokens ${STATE_DIR}/tokens.json · logs ${STATE_DIR}/api.log"
}

verify() {
  log "Ledger / balance / reservation invariants (read-only, superuser)"
  psql_super -q -f - <"${REPO_ROOT}/tools/load/verify.sql"
}

down() {
  stop_bg api
  stop_bg auth
  docker rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true
}

case "${1:-}" in
  up) up ;;
  verify) verify ;;
  down) down ;;
  *) echo "usage: $0 up|verify|down" >&2; exit 2 ;;
esac
