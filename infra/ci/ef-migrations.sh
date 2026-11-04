#!/usr/bin/env bash
# EF Core migration check (ADR 0005 §4, deploy-and-rollback.md §5). Used by .github/workflows/ci-migrations.yml
# and runnable locally from the repo root (needs bash, dotnet SDK and Docker):
#
#   bash infra/ci/ef-migrations.sh
#
# For every module DbContext (discovered, not listed by hand) it:
#   1. generates `dotnet ef migrations script --idempotent` into $OUT_DIR (the reviewable deploy artefact),
#   2. fails if `dotnet ef migrations has-pending-model-changes` reports model changes without a migration,
#   3. applies every script to an empty postgres:17 bootstrapped with infra/docker/initdb, as bayios_migrator,
#   4. applies every script a second time to prove it is idempotent, and checks the migration history.
#
# No secrets: the database is a throwaway container bound to 127.0.0.1 with local-only passwords.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
API_DIR="${REPO_ROOT}/services/api"
OUT_DIR="${OUT_DIR:-${RUNNER_TEMP:-${TMPDIR:-/tmp}}/bayios-migrations}"
PG_IMAGE="${PG_IMAGE:-postgres:17}"
PG_CONTAINER="${PG_CONTAINER:-bayios-migrations-check}"
PG_DB="bayios"
MIGRATOR_PASSWORD="${BAYIOS_MIGRATOR_PASSWORD:-ci-local-only}"
APP_PASSWORD="${BAYIOS_APP_PASSWORD:-ci-local-only}"
SUPERUSER_PASSWORD="${POSTGRES_PASSWORD:-ci-local-only}"

log() { printf '\n==> %s\n' "$*"; }
fail() { printf '::error::%s\n' "$*" >&2; exit 1; }

# Git Bash on Windows rewrites /paths in arguments; Docker needs a native path for the bind mount.
native_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

cleanup() { docker rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true; }
trap cleanup EXIT

# ---------------------------------------------------------------- dotnet-ef pinned to the repo's EF Core version
ef_version="$(sed -n 's/.*Include="Microsoft.EntityFrameworkCore.Design" Version="\([^"]*\)".*/\1/p' "${API_DIR}/Directory.Packages.props")"
[ -n "$ef_version" ] || fail "Microsoft.EntityFrameworkCore.Design version not found in Directory.Packages.props"

tool_dir="${EF_TOOL_DIR:-${RUNNER_TEMP:-${TMPDIR:-/tmp}}/bayios-dotnet-ef-${ef_version}}"
if [ ! -x "${tool_dir}/dotnet-ef" ] && [ ! -x "${tool_dir}/dotnet-ef.exe" ]; then
  log "Installing dotnet-ef ${ef_version} into ${tool_dir}"
  dotnet tool install dotnet-ef --version "$ef_version" --tool-path "$tool_dir" >/dev/null
fi
# Call the pinned tool by path so a globally installed dotnet-ef of another version is never used.
dotnet-ef() { "${tool_dir}/dotnet-ef" "$@"; }
installed="$(dotnet-ef --version | tail -n 1 | tr -d '\r')"
[ "$installed" = "$ef_version" ] || fail "dotnet-ef ${installed} does not match EF Core ${ef_version}"
log "dotnet-ef ${installed} (EF Core ${ef_version})"

# ---------------------------------------------------------------- discover module projects with EF design-time support
mapfile -t projects < <(grep -l --include='*.csproj' -r 'Microsoft.EntityFrameworkCore.Design' "${API_DIR}/src/Modules" | sort)
[ "${#projects[@]}" -gt 0 ] || fail "No module project references Microsoft.EntityFrameworkCore.Design"

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

log "Restoring and building the API solution"
dotnet build "${API_DIR}/BayiOS.slnx" -c Release -v quiet -nologo

scripts=()
for project in "${projects[@]}"; do
  module="$(basename "$project" .csproj)"
  # The module is its own startup project: each DbContext ships an IDesignTimeDbContextFactory.
  ef_args=(--project "$project" --startup-project "$project" --configuration Release --no-build)

  mapfile -t contexts < <(dotnet-ef dbcontext list "${ef_args[@]}" --json --prefix-output \
    | sed -n 's/^data:[[:space:]]*//p' | grep -o '"safeName": *"[^"]*"' | sed 's/.*"\([^"]*\)"$/\1/' | tr -d '\r')
  if [ "${#contexts[@]}" -eq 0 ]; then
    echo "${module}: no DbContext, skipping"
    continue
  fi

  for context in "${contexts[@]}"; do
    name="${module}.${context}"
    log "${name}: pending model changes check"
    if ! dotnet-ef migrations has-pending-model-changes "${ef_args[@]}" --context "$context"; then
      fail "${name}: the model has changes that are not in a migration. Run 'dotnet ef migrations add' for this module."
    fi

    log "${name}: idempotent migration script"
    out="${OUT_DIR}/${name}.sql"
    dotnet-ef migrations script --idempotent "${ef_args[@]}" --context "$context" --output "$out"
    [ -s "$out" ] || fail "${name}: generated script is empty"
    scripts+=("$out")
  done
done
[ "${#scripts[@]}" -gt 0 ] || fail "No DbContext found in any module"

# ---------------------------------------------------------------- empty postgres:17 bootstrapped like local/CI databases
log "Starting ${PG_IMAGE} with infra/docker/initdb"
cleanup
MSYS_NO_PATHCONV=1 docker run -d --name "$PG_CONTAINER" \
  -e POSTGRES_DB="$PG_DB" -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD="$SUPERUSER_PASSWORD" \
  -e BAYIOS_APP_PASSWORD="$APP_PASSWORD" -e BAYIOS_MIGRATOR_PASSWORD="$MIGRATOR_PASSWORD" \
  -v "$(native_path "${REPO_ROOT}/infra/docker/initdb"):/docker-entrypoint-initdb.d:ro" \
  "$PG_IMAGE" >/dev/null

# TCP readiness only becomes true after the init scripts ran (the init server listens on the socket only).
for _ in $(seq 1 60); do
  if docker exec "$PG_CONTAINER" pg_isready -h 127.0.0.1 -U postgres -d "$PG_DB" >/dev/null 2>&1; then ready=1; break; fi
  sleep 1
done
[ "${ready:-0}" = 1 ] || { docker logs "$PG_CONTAINER"; fail "postgres did not become ready"; }

psql_migrator() {
  MSYS_NO_PATHCONV=1 docker exec -i -e PGPASSWORD="$MIGRATOR_PASSWORD" "$PG_CONTAINER" \
    psql -h 127.0.0.1 -U bayios_migrator -d "$PG_DB" -v ON_ERROR_STOP=1 -q "$@"
}

psql_migrator -tAc 'select current_user' | grep -qx bayios_migrator || fail "cannot log in as bayios_migrator"

for pass in 1 2; do
  for script in "${scripts[@]}"; do
    log "Apply (pass ${pass}) $(basename "$script") as bayios_migrator"
    psql_migrator -f - < "$script" >/dev/null
  done
done

log "Migration history per schema"
psql_migrator -c "
  select n.nspname as schema, count(c.oid) as tables
  from pg_namespace n join pg_class c on c.relnamespace = n.oid and c.relkind = 'r'
  where pg_get_userbyid(n.nspowner) = 'bayios_migrator'
  group by n.nspname order by 1;"

for script in "${scripts[@]}"; do
  # Column names follow the naming convention (snake_case here), so accept both spellings.
  expected="$(grep -oE "\"(migration_id|MigrationId)\" = '[^']*'" "$script" | sort -u | wc -l | tr -d ' ')"
  schema="$(grep -oE 'CREATE TABLE IF NOT EXISTS [a-z_]+\.__ef_migrations_history' "$script" | head -n 1 \
    | sed -E 's/.* ([a-z_]+)\.__ef_migrations_history/\1/')"
  [ -n "$schema" ] || fail "$(basename "$script"): migration history table not found in script"
  applied="$(psql_migrator -tAc "select count(*) from ${schema}.__ef_migrations_history" | tr -d '[:space:]')"
  echo "$(basename "$script"): ${applied}/${expected} migrations recorded in ${schema}.__ef_migrations_history"
  [ "$applied" = "$expected" ] || fail "$(basename "$script"): expected ${expected} applied migrations, found ${applied}"
done

log "OK: ${#scripts[@]} idempotent script(s) in ${OUT_DIR}"
