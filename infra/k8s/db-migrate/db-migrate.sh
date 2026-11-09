#!/usr/bin/env bash
# Kubernetes Job entrypoint (infra/helm/bayios/templates/db-migrate-job.yaml). Order is the deploy order of
# docs/operations/deploy-and-rollback.md: bootstrap -> role passwords -> migrations (as bayios_migrator) -> verify.
#
# Env (from the chart's Secret/values; nothing is printed):
#   PGHOST, PGPORT, PGDATABASE   in-cluster PostgreSQL service
#   POSTGRES_PASSWORD            superuser `postgres` (bootstrap only; this in-cluster DB is a kind/dev rehearsal)
#   BAYIOS_APP_PASSWORD, BAYIOS_MIGRATOR_PASSWORD
#   DB_WAIT_SECONDS              optional, default 180
#
# Plain-TCP connection (ALLOW_INSECURE_DB_CONNECTION=1) is acceptable only because the database is a throwaway
# in-cluster pod reached over the cluster network. The hosted (Supabase) path keeps sslmode=require.
set -euo pipefail

: "${PGHOST:?}" "${PGPORT:=5432}" "${PGDATABASE:?}"
: "${POSTGRES_PASSWORD:?}" "${BAYIOS_APP_PASSWORD:?}" "${BAYIOS_MIGRATOR_PASSWORD:?}"
ROOT=/bayios
log() { printf '\n==> %s\n' "$*"; }

# Percent-encode a password for a libpq URI (RFC 3986 unreserved characters pass through).
urlencode() {
  local s="$1" out="" c i
  for (( i = 0; i < ${#s}; i++ )); do
    c="${s:i:1}"
    case "$c" in [a-zA-Z0-9._~-]) out+="$c" ;; *) out+="$(printf '%%%02X' "'$c")" ;; esac
  done
  printf '%s' "$out"
}
uri() { printf 'postgresql://%s:%s@%s:%s/%s?sslmode=disable' "$1" "$(urlencode "$2")" "$PGHOST" "$PGPORT" "$PGDATABASE"; }

log "Waiting for PostgreSQL at ${PGHOST}:${PGPORT}"
# An authenticated query, twice a few seconds apart: on first start the official image runs initdb, a temporary
# server and then a restart, so a single successful probe can land just before that restart. Any remaining race
# only fails this attempt; every step below is idempotent and the Job retries (backoffLimit).
db_up() { PGPASSWORD="$POSTGRES_PASSWORD" PGCONNECT_TIMEOUT=5 psql -X -q -U postgres -tAc 'select 1' >/dev/null 2>&1; }
deadline=$(( $(date +%s) + ${DB_WAIT_SECONDS:-180} ))
until db_up && sleep 3 && db_up; do
  [ "$(date +%s)" -lt "$deadline" ] || { echo "PostgreSQL not ready in time" >&2; exit 1; }
  sleep 2
done

export ALLOW_INSECURE_DB_CONNECTION=1

log "Bootstrap (roles, extensions, verify) as postgres"
SUPABASE_DB_URL="$(uri postgres "$POSTGRES_PASSWORD")" bash "${ROOT}/infra/supabase/bootstrap/bootstrap.sh"

log "Role passwords from the Secret"
# psql variables (:'x') quote the values; nothing reaches the command line or the log.
PGPASSWORD="$POSTGRES_PASSWORD" psql -X -q -v ON_ERROR_STOP=1 -U postgres \
  -v app_password="$BAYIOS_APP_PASSWORD" -v migrator_password="$BAYIOS_MIGRATOR_PASSWORD" <<'SQL'
ALTER ROLE bayios_app PASSWORD :'app_password';
ALTER ROLE bayios_migrator PASSWORD :'migrator_password';
SQL

log "EF Core migrations as bayios_migrator"
MIGRATOR_URL="$(uri bayios_migrator "$BAYIOS_MIGRATOR_PASSWORD")" SCRIPTS_DIR="${ROOT}/migrations" \
  bash "${ROOT}/infra/deploy/apply-migrations.sh"

log "OK: database bootstrapped and migrated"
