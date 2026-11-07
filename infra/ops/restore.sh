#!/usr/bin/env bash
# BayiOS restore into a FRESH database (#55, docs/operations/backup-and-restore.md).
#
#   RESTORE_ADMIN_URL='postgresql://postgres@<host>:5432/postgres' PGPASSWORD=… \
#   RESTORE_DATABASE=bayios_restore_20261107 RESTORE_CONFIRM=bayios_restore_20261107 \
#   BAYIOS_APP_PASSWORD=… BAYIOS_MIGRATOR_PASSWORD=… \
#   bash infra/ops/restore.sh /secure/backups/<label>.dump
#
# Steps: checksum → CREATE DATABASE (must not exist) → roles bayios_migrator / bayios_app from
# infra/docker/initdb/01-roles.sql (+ passwords when given) → extensions from the manifest → pg_restore
# --single-transaction --exit-on-error (owners and GRANTs as dumped: tables owned by bayios_migrator, bayios_app keeps
# only its grants) → verification:
#   * row count per table equals the manifest (RESTORE_COUNTS=exact, default) or is only reported (=report);
#   * RLS enabled/forced flags per table equal the manifest;
#   * bayios_app is NOSUPERUSER NOBYPASSRLS and, without app.tenant_id, sees no tenant row in any RLS table;
#   * with app.tenant_id set to a tenant, bayios_app sees no row of another tenant in any RLS table;
#   * EF migration history per schema equals the manifest.
# Never restores over an existing database and never touches another database on the server.
#
# Env: RESTORE_ADMIN_URL (superuser/admin on the target server, any database), RESTORE_DATABASE, RESTORE_CONFIRM
#      (= RESTORE_DATABASE), BAYIOS_APP_PASSWORD / BAYIOS_MIGRATOR_PASSWORD (optional: set when the roles are created
#      here and must log in; local/throwaway servers only), RESTORE_COUNTS (exact|report),
#      RESTORE_TARGET (new = create database + roles, default | bootstrapped = an existing, empty database already
#      prepared by infra/supabase/bootstrap/bootstrap.sh — the hosted Supabase path; RESTORE_ADMIN_URL then points at
#      that database), PG_TOOLS / PG_DOCKER_IMAGE, PGPASSWORD.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
# shellcheck source=infra/ops/pg-tools.sh
. "${SCRIPT_DIR}/pg-tools.sh"

DUMP="${1:?usage: restore.sh <backup>.dump}"
ADMIN_URL="${RESTORE_ADMIN_URL:?RESTORE_ADMIN_URL is required}"
DB="${RESTORE_DATABASE:?RESTORE_DATABASE is required}"
COUNTS="${RESTORE_COUNTS:-exact}"
[[ "$DB" =~ ^[a-z_][a-z0-9_]*$ ]] || fail "RESTORE_DATABASE must be a lower-case identifier"
[ "${RESTORE_CONFIRM:-}" = "$DB" ] || fail "set RESTORE_CONFIRM=${DB} to confirm the target database name"
MANIFEST="${DUMP%.dump}.manifest"
[ -s "$DUMP" ] && [ -s "$MANIFEST" ] || fail "need ${DUMP} and ${MANIFEST}"

# The admin URL with the database replaced (postgresql://user@host:port/db?params).
TARGET_URL="$(printf '%s' "$ADMIN_URL" | sed -E "s#^(postgres(ql)?://[^/]+)/[^?]*#\1/${DB}#")"
[ "$TARGET_URL" != "$ADMIN_URL" ] || TARGET_URL="${ADMIN_URL%/}/${DB}"

if [ -s "${DUMP%.dump}.sha256" ]; then
  log "Checksum"
  [ "$(sha256_of "$DUMP")" = "$(cat "${DUMP%.dump}.sha256")" ] || fail "checksum mismatch: ${DUMP} is damaged"
  echo "sha256 ok"
fi

schemas="$(awk -F'\t' '$1 == "schemas" { print $2 }' "$MANIFEST")"
[[ "$schemas" =~ ^[a-z_][a-z0-9_,]*$ ]] || fail "manifest has no valid schema list"

if [ "${RESTORE_TARGET:-new}" = bootstrapped ]; then
  # A fresh Supabase project (or any server) already prepared with infra/supabase/bootstrap/bootstrap.sh
  # (roles, extensions, auth hook, passwords via \password): restore into that database, which must hold none of the
  # business schemas yet.
  log "Using the bootstrapped database ${DB}"
  TARGET_URL="$ADMIN_URL"
  [ "$(psql_at "$TARGET_URL" "SELECT current_database()")" = "$DB" ] || fail "RESTORE_ADMIN_URL must point at database ${DB}"
  present="$(psql_at "$TARGET_URL" "SELECT count(*) FROM pg_namespace WHERE nspname = ANY (string_to_array('${schemas}', ','))")"
  [ "$present" = 0 ] || fail "${DB} already contains ${present} BayiOS schema(s) — restore only into an empty project"
  [ "$(psql_at "$TARGET_URL" "SELECT count(*) FROM pg_roles WHERE rolname IN ('bayios_app', 'bayios_migrator')")" = 2 ] \
    || fail "roles missing: run infra/supabase/bootstrap/bootstrap.sh --set-passwords first"
else
  log "Creating database ${DB}"
  exists="$(psql_at "$ADMIN_URL" "SELECT count(*) FROM pg_database WHERE datname = '${DB}'")"
  [ "$exists" = 0 ] || fail "database ${DB} already exists — restore only into a fresh database"
  psql_at "$ADMIN_URL" "CREATE DATABASE ${DB}" >/dev/null

  log "Roles (infra/docker/initdb/01-roles.sql)"
  pg_tool psql -X -q -v ON_ERROR_STOP=1 -d "$TARGET_URL" < "${REPO_ROOT}/infra/docker/initdb/01-roles.sql"
  if [ -n "${BAYIOS_APP_PASSWORD:-}" ] && [ -n "${BAYIOS_MIGRATOR_PASSWORD:-}" ]; then
    # Throwaway/local servers only: ALTER ROLE … PASSWORD reaches a server log when log_statement = ddl (Supabase).
    # Hosted targets use RESTORE_TARGET=bootstrapped (passwords set by bootstrap.sh via \password, SCRAM verifier only).
    # Passwords travel on stdin as psql variables, never on a command line.
    printf '%s\n' "\\set app_password '${BAYIOS_APP_PASSWORD//\'/\'\'}'" "\\set migrator_password '${BAYIOS_MIGRATOR_PASSWORD//\'/\'\'}'" \
      "ALTER ROLE bayios_app PASSWORD :'app_password';" "ALTER ROLE bayios_migrator PASSWORD :'migrator_password';" \
      | pg_tool psql -X -q -v ON_ERROR_STOP=1 -d "$TARGET_URL" >/dev/null
    echo "role passwords set"
  fi
fi

log "Extensions from the manifest"
while IFS=$'\t' read -r _ name schema; do
  [[ "$name" =~ ^[a-z0-9_]+$ && "$schema" =~ ^[a-z_][a-z0-9_]*$ ]] || fail "bad extension line: ${name} ${schema}"
  # As infra/supabase/bootstrap/02-extensions.sql: the migrator needs USAGE to resolve operator classes in later
  # migrations; bayios_app gets nothing.
  psql_at "$TARGET_URL" "CREATE SCHEMA IF NOT EXISTS ${schema}; CREATE EXTENSION IF NOT EXISTS ${name} WITH SCHEMA ${schema}; GRANT USAGE ON SCHEMA ${schema} TO bayios_migrator" >/dev/null
  echo "${name} in ${schema}"
done < <(awk -F'\t' '$1 == "extension"' "$MANIFEST")

log "pg_restore (single transaction)"
started=$(date +%s)
pg_tool pg_restore --exit-on-error --single-transaction --no-password --dbname="$TARGET_URL" < "$DUMP"
echo "restored in $(( $(date +%s) - started )) s"

failures=0
check() { if [ "$2" = "$3" ]; then printf '  ok   %s\n' "$1"; else printf '  FAIL %s (expected %s, got %s)\n' "$1" "$2" "$3"; failures=$((failures + 1)); fi; }

log "Verify: row counts"
actual_counts="$(psql_at "$TARGET_URL" "$(table_counts_sql "$schemas")")"
expected_counts="$(awk -F'\t' '$1 == "count" { print $2 "\t" $3 }' "$MANIFEST")"
count_diff="$(diff <(printf '%s\n' "$expected_counts") <(printf '%s\n' "$actual_counts") || true)"
tables="$(printf '%s\n' "$actual_counts" | grep -c . || true)"
rows="$(printf '%s\n' "$actual_counts" | awk -F'\t' '{ s += $2 } END { print s + 0 }')"
if [ -z "$count_diff" ]; then
  echo "  ok   ${tables} tables, ${rows} rows — identical to the manifest"
elif [ "$COUNTS" = report ]; then
  printf '  note counts differ from the manifest (RESTORE_COUNTS=report):\n%s\n' "$count_diff"
else
  printf '  FAIL counts differ from the manifest:\n%s\n' "$count_diff"; failures=$((failures + 1))
fi

log "Verify: RLS flags"
actual_rls="$(psql_at "$TARGET_URL" "$(rls_flags_sql "$schemas")")"
rls_diff="$(diff <(awk -F'\t' '$1 == "rls" { print $2 "\t" $3 "\t" $4 }' "$MANIFEST") <(printf '%s\n' "$actual_rls") || true)"
check "RLS enabled/forced per table as in the source (diff empty)" "" "$rls_diff"
forced="$(printf '%s\n' "$actual_rls" | awk -F'\t' '$3 == "true"' | grep -c . || true)"
echo "  info ${forced} tables with FORCE ROW LEVEL SECURITY"

log "Verify: EF migration history"
while IFS=$'\t' read -r _ schema last; do
  check "${schema} last migration" "$last" "$(psql_at "$TARGET_URL" "SELECT max(migration_id) FROM ${schema}.__ef_migrations_history")"
done < <(awk -F'\t' '$1 == "migration"' "$MANIFEST")

log "Verify: bayios_app is still bound by RLS"
check "bayios_app NOSUPERUSER NOBYPASSRLS" "f f" \
  "$(psql_at "$TARGET_URL" "SELECT CASE WHEN rolsuper THEN 't' ELSE 'f' END || ' ' || CASE WHEN rolbypassrls THEN 't' ELSE 'f' END FROM pg_roles WHERE rolname = 'bayios_app'")"
# Tenant tables = RLS-enabled tables with a tenant_id column that bayios_app may SELECT.
tenant_tables="$(psql_at "$TARGET_URL" "
  SELECT string_agg(format('%I.%I', n.nspname, c.relname), ' ' ORDER BY 1)
  FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
  JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped
  WHERE c.relkind IN ('r', 'p') AND c.relrowsecurity AND n.nspname = ANY (string_to_array('${schemas}', ','))
    AND has_table_privilege('bayios_app', c.oid, 'SELECT')")"
sample_tenant="$(psql_at "$TARGET_URL" "SELECT id FROM identity.tenants ORDER BY created_at LIMIT 1" 2>/dev/null || true)"
visible_without_tenant=0
foreign_visible=0
own_visible=0
for table in $tenant_tables; do
  # SET ROLE inside one transaction: the admin session impersonates bayios_app (no password needed for the check).
  n="$(psql_at "$TARGET_URL" "BEGIN; SET LOCAL ROLE bayios_app; SELECT count(*) FROM ${table} WHERE tenant_id IS NOT NULL; ROLLBACK;" | grep -E '^[0-9]+$' || echo error)"
  [ "$n" = 0 ] || { echo "  FAIL ${table}: ${n} tenant rows visible to bayios_app without app.tenant_id"; visible_without_tenant=$((visible_without_tenant + 1)); }
  if [ -n "$sample_tenant" ]; then
    r="$(psql_at "$TARGET_URL" "BEGIN; SET LOCAL ROLE bayios_app; SELECT set_config('app.tenant_id', '${sample_tenant}', true); SELECT count(*) FILTER (WHERE tenant_id <> '${sample_tenant}') || ' ' || count(*) FILTER (WHERE tenant_id = '${sample_tenant}') FROM ${table}; ROLLBACK;" | grep -E '^[0-9]+ [0-9]+$' || echo "error 0")"
    [ "${r% *}" = 0 ] || { echo "  FAIL ${table}: ${r% *} rows of another tenant visible"; foreign_visible=$((foreign_visible + 1)); }
    own_visible=$((own_visible + ${r#* }))
  fi
done
check "tenant tables checked: $(wc -w <<< "$tenant_tables" | tr -d ' '); tables leaking rows without a tenant" 0 "$visible_without_tenant"
if [ -n "$sample_tenant" ]; then
  check "tables leaking another tenant's rows (tenant ${sample_tenant})" 0 "$foreign_visible"
  echo "  info rows of tenant ${sample_tenant} visible to it across tenant tables: ${own_visible}"
fi

[ "$failures" -eq 0 ] && [ "$visible_without_tenant" -eq 0 ] && [ "$foreign_visible" -eq 0 ] \
  || fail "restore verification failed (${failures} check(s)); the database ${DB} is kept for investigation"
echo
echo "OK: ${DUMP} restored into ${DB} and verified"
