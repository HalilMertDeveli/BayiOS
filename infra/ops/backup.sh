#!/usr/bin/env bash
# BayiOS logical backup (#55, docs/operations/backup-and-restore.md).
#
#   BACKUP_DATABASE_URL='postgresql://<admin user>@<host>:5432/<db>?sslmode=require' PGPASSWORD=… \
#   BACKUP_DIR=/secure/backups bash infra/ops/backup.sh [label]
#
# Writes, outside the repository:
#   <label>.dump      pg_dump custom format (-Fc, compressed) of the BayiOS business schemas only
#   <label>.manifest  schemas, required extensions, row count + RLS flags per table, last EF migration per schema
#   <label>.sha256    checksum of the dump
#
# Scope: every schema owned by BACKUP_SCHEMA_OWNER (default bayios_migrator — the business schemas of ADR 0005 incl.
# their __ef_migrations_history), except `extensions`. Roles, Supabase-managed schemas (auth, storage, …) and
# extensions are NOT in the dump: restore.sh recreates roles from infra/docker/initdb/01-roles.sql and extensions from
# the manifest. Supabase Auth users live in `auth` and are covered by the Supabase platform backup (see the doc).
#
# The connecting role must see every row: superuser or BYPASSRLS (FORCE RLS makes pg_dump fail otherwise). Never the
# application role bayios_app. Row counts are taken just before the dump; on a busy database they can differ
# slightly from the dump — quiesce writes (drill, maintenance window) for an exact manifest.
#
# Env: BACKUP_DATABASE_URL (required), BACKUP_DIR (default ${TMPDIR:-/tmp}/bayios-backups), BACKUP_SCHEMA_OWNER,
#      PG_TOOLS / PG_DOCKER_IMAGE (see pg-tools.sh), PGPASSWORD.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=infra/ops/pg-tools.sh
. "${SCRIPT_DIR}/pg-tools.sh"

URL="${BACKUP_DATABASE_URL:?BACKUP_DATABASE_URL is required (admin/BYPASSRLS role, never bayios_app)}"
OWNER="${BACKUP_SCHEMA_OWNER:-bayios_migrator}"
DIR="${BACKUP_DIR:-${TMPDIR:-/tmp}/bayios-backups}"
LABEL="${1:-bayios-$(date -u +%Y%m%dT%H%M%SZ)}"
[[ "$LABEL" =~ ^[A-Za-z0-9._-]+$ ]] || fail "label may contain letters, digits, '.', '_' and '-' only"

mkdir -p "$DIR"
DIR="$(cd "$DIR" && pwd)"
refuse_repo_path "$DIR"
DUMP="${DIR}/${LABEL}.dump"
MANIFEST="${DIR}/${LABEL}.manifest"
[ ! -e "$DUMP" ] || fail "${DUMP} already exists"

log "Checking the connecting role"
role="$(psql_at "$URL" "SELECT current_user || ' ' || CASE WHEN rolsuper OR rolbypassrls THEN 't' ELSE 'f' END FROM pg_roles WHERE rolname = current_user")"
[ "${role##* }" = t ] || fail "role '${role% *}' can neither bypass RLS nor is superuser; pg_dump would fail on FORCE RLS tables"
[ "${role% *}" != bayios_app ] || fail "never back up as bayios_app"
server="$(psql_at "$URL" "SHOW server_version")"
echo "role ${role% *}, server ${server}"

log "Discovering schemas owned by ${OWNER}"
schemas="$(psql_at "$URL" "SELECT string_agg(nspname, ',' ORDER BY nspname) FROM pg_namespace WHERE pg_get_userbyid(nspowner) = '${OWNER//\'/}' AND nspname <> 'extensions'")"
[ -n "$schemas" ] || fail "no schema owned by ${OWNER} — wrong database?"
[[ "$schemas" =~ ^[a-z_][a-z0-9_,]*$ ]] || fail "unexpected schema names: ${schemas}"
echo "$schemas"

log "Writing manifest ${MANIFEST}"
{
  printf 'format\tbayios-backup-1\n'
  printf 'created_at\t%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf 'server_version\t%s\n' "$server"
  printf 'schemas\t%s\n' "$schemas"
  # Extensions that business objects depend on (e.g. pg_trgm operator classes in catalog indexes).
  psql_at "$URL" "SELECT 'extension' || E'\t' || e.extname || E'\t' || n.nspname FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname <> 'plpgsql' ORDER BY 1"
  psql_at "$URL" "$(table_counts_sql "$schemas")" | sed 's/^/count\t/'
  psql_at "$URL" "$(rls_flags_sql "$schemas")" | sed 's/^/rls\t/'
  for schema in ${schemas//,/ }; do
    if [ "$(psql_at "$URL" "SELECT to_regclass('${schema}.__ef_migrations_history') IS NOT NULL")" = t ]; then
      printf 'migration\t%s\t%s\n' "$schema" "$(psql_at "$URL" "SELECT max(migration_id) FROM ${schema}.__ef_migrations_history")"
    fi
  done
} > "$MANIFEST"

log "pg_dump (custom format) → ${DUMP}"
schema_args=()
for schema in ${schemas//,/ }; do schema_args+=(--schema="$schema"); done
started=$(date +%s)
pg_tool pg_dump --format=custom --compress=6 --no-password --dbname="$URL" "${schema_args[@]}" > "$DUMP"
[ -s "$DUMP" ] || fail "empty dump"
sha256_of "$DUMP" > "${DIR}/${LABEL}.sha256"

tables="$(grep -c '^count' "$MANIFEST")"
rows="$(awk -F'\t' '$1 == "count" { s += $3 } END { print s + 0 }' "$MANIFEST")"
echo "OK: ${DUMP} ($(wc -c < "$DUMP") bytes, $(( $(date +%s) - started )) s), ${tables} tables, ${rows} rows, sha256 $(cat "${DIR}/${LABEL}.sha256")"
