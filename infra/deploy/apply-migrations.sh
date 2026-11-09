#!/usr/bin/env bash
# Deploy-time migration step (issue #70, ADR 0005 §4/§6, deploy-and-rollback.md §5).
#
# Applies the idempotent EF Core scripts produced by infra/ci/ef-migrations.sh to a target database AS bayios_migrator,
# in the order recorded in manifest.txt, then runs the posture check infra/supabase/bootstrap/verify.sql.
# Used by .github/workflows/deploy-preview.yml / deploy-production.yml BEFORE the API is deployed, and by
# infra/supabase/bootstrap/test.sh (rehearsal against throwaway containers).
#
# Env:
#   MIGRATOR_URL   libpq URI of the target, user bayios_migrator (Supabase pooler form: bayios_migrator.<project-ref>),
#                  e.g. postgresql://bayios_migrator.<ref>:<pw>@aws-0-eu-central-1.pooler.supabase.com:5432/postgres?sslmode=require
#                  Never printed. Must carry sslmode=require|verify-ca|verify-full.
#   SCRIPTS_DIR    directory with *.sql + manifest.txt from ef-migrations.sh (OUT_DIR there).
#   VERIFY_SQL     optional, defaults to infra/supabase/bootstrap/verify.sql in this repo.
#   ALLOW_INSECURE_DB_CONNECTION=1  only for throwaway local/CI containers (no TLS); never set in a deploy workflow.
#
# Fails closed: wrong role, missing TLS, missing manifest, any SQL error or posture violation => non-zero exit, and the
# deploy workflow stops before the API is touched. Needs: bash, psql (PostgreSQL client >= 15).
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
VERIFY_SQL="${VERIFY_SQL:-${REPO_ROOT}/infra/supabase/bootstrap/verify.sql}"

log() { printf '\n==> %s\n' "$*"; }
fail() { printf '::error::%s\n' "$*" >&2; exit 1; }

[ -n "${MIGRATOR_URL:-}" ] || fail "MIGRATOR_URL is empty"
[ -n "${SCRIPTS_DIR:-}" ] && [ -d "$SCRIPTS_DIR" ] || fail "SCRIPTS_DIR '${SCRIPTS_DIR:-}' is not a directory"
command -v psql >/dev/null 2>&1 || fail "psql not found (install postgresql-client)"
[ -f "$VERIFY_SQL" ] || fail "verify SQL not found: ${VERIFY_SQL}"

# ---------------------------------------------------------------- connection guard rails (no value is ever printed)
case "$MIGRATOR_URL" in
  postgres://*|postgresql://*) ;;
  *) fail "MIGRATOR_URL must be a postgresql:// URI" ;;
esac
url_user="$(printf '%s' "$MIGRATOR_URL" | sed -E 's#^[a-z]+://([^:@/]*).*#\1#')"
case "$url_user" in
  bayios_migrator|bayios_migrator.*) ;;
  *) fail "MIGRATOR_URL must log in as bayios_migrator (or bayios_migrator.<project-ref> via the Supabase pooler); refusing other roles" ;;
esac
if [ "${ALLOW_INSECURE_DB_CONNECTION:-0}" != 1 ]; then
  printf '%s' "$MIGRATOR_URL" | grep -Eq '[?&]sslmode=(require|verify-ca|verify-full)(&|$)' \
    || fail "MIGRATOR_URL must contain sslmode=require (or verify-ca / verify-full)"
fi

export PGCONNECT_TIMEOUT="${PGCONNECT_TIMEOUT:-15}"
export PGAPPNAME="bayios-deploy-migrations"
psql_migrator() { psql "$MIGRATOR_URL" -X -v ON_ERROR_STOP=1 -q "$@"; }

who="$(psql_migrator -tAc 'select current_user' | tr -d '[:space:]')"
[ "$who" = bayios_migrator ] || fail "connected as '${who}', expected bayios_migrator"
log "Connected as bayios_migrator ($(psql_migrator -tAc 'show server_version' | tr -d '[:space:]'))"

# ---------------------------------------------------------------- apply in manifest order
manifest="${SCRIPTS_DIR}/manifest.txt"
[ -s "$manifest" ] || fail "manifest.txt missing or empty in SCRIPTS_DIR (produced by infra/ci/ef-migrations.sh)"
mapfile -t scripts < <(tr -d '\r' < "$manifest" | sed '/^[[:space:]]*$/d')
[ "${#scripts[@]}" -gt 0 ] || fail "manifest.txt lists no scripts"

for name in "${scripts[@]}"; do
  case "$name" in */*|..*) fail "manifest entry '${name}' must be a plain file name" ;; esac
  [ -s "${SCRIPTS_DIR}/${name}" ] || fail "script ${name} listed in manifest.txt is missing or empty"
done

for name in "${scripts[@]}"; do
  log "Apply ${name}"
  # Each EF migration in an idempotent script runs in its own transaction and is skipped if already recorded.
  psql_migrator -f "${SCRIPTS_DIR}/${name}" >/dev/null
done

# ---------------------------------------------------------------- history check (same rule as ef-migrations.sh)
for name in "${scripts[@]}"; do
  script="${SCRIPTS_DIR}/${name}"
  expected="$(grep -oE "\"(migration_id|MigrationId)\" = '[^']*'" "$script" | sort -u | wc -l | tr -d ' ')"
  schema="$(grep -oE 'CREATE TABLE IF NOT EXISTS [a-z_]+\.__ef_migrations_history' "$script" | head -n 1 \
    | sed -E 's/.* ([a-z_]+)\.__ef_migrations_history/\1/')"
  [ -n "$schema" ] || fail "${name}: migration history table not found in script"
  applied="$(psql_migrator -tAc "select count(*) from ${schema}.__ef_migrations_history" | tr -d '[:space:]')"
  echo "${name}: ${applied}/${expected} migrations recorded in ${schema}.__ef_migrations_history"
  [ "$applied" -ge "$expected" ] || fail "${name}: expected at least ${expected} applied migrations, found ${applied}"
done

# ---------------------------------------------------------------- posture check (RLS, grants, role attributes)
log "Posture check (verify.sql) as bayios_migrator"
psql_migrator -f "$VERIFY_SQL"

log "OK: ${#scripts[@]} migration script(s) applied"
