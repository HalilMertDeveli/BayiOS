#!/usr/bin/env bash
# BayiOS RESTORE DRILL (#55, docs/operations/backup-and-restore.md §4). Local, synthetic data only, two throwaway
# containers — never touches the owner's stack (bayios-e2e-postgres, Supabase local) or any hosted database.
#
#   bash infra/ops/restore-drill.sh            # from the repo root; needs bash, Docker and the .NET SDK
#
#  1. migration scripts: `infra/ci/ef-migrations.sh` (pending-model check + idempotent scripts), or MIGRATIONS_DIR
#  2. SOURCE  postgres:17 (bootstrapped with infra/docker/initdb) ← all module migrations as bayios_migrator
#  3. seed    infra/ops/sql/drill-seed.sql: two synthetic tenants across identity, catalog, warehouse, inventory
#             (ledger + balances), customers, orders, platform and audit
#  4. backup  infra/ops/backup.sh (pg_dump -Fc + manifest)
#  5. TARGET  an empty postgres:17 (no roles, no init scripts) ← infra/ops/restore.sh (roles, extensions, pg_restore,
#             counts, RLS flags, migration history, bayios_app tenant isolation)
#  6. drill checks with a real bayios_app login: per-tenant visibility, cross-tenant write refused by RLS, ledger
#     reconciliation (balances = sum of movements) and per-tenant content fingerprints equal in source and target.
#
# Env: DRILL_SOURCE_PORT (55641), DRILL_TARGET_PORT (55642), PG_IMAGE (postgres:17), MIGRATIONS_DIR, DRILL_WORK_DIR,
#      KEEP_CONTAINERS=1 to keep both containers for inspection.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OPS="${REPO_ROOT}/infra/ops"
# shellcheck source=infra/ops/pg-tools.sh
. "${OPS}/pg-tools.sh"

PG_IMAGE="${PG_IMAGE:-postgres:17}"
SRC="bayios-drill-source"
TGT="bayios-drill-target"
SRC_PORT="${DRILL_SOURCE_PORT:-55641}"
TGT_PORT="${DRILL_TARGET_PORT:-55642}"
WORK="${DRILL_WORK_DIR:-${TMPDIR:-/tmp}/bayios-restore-drill}"
SUPER_PASSWORD="drill-local-only"
APP_PASSWORD="drill-app-local-only"
MIGRATOR_PASSWORD="drill-migrator-local-only"
RESTORED_DB="bayios_restored"

native_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
cleanup() {
  if [ "${KEEP_CONTAINERS:-0}" != 1 ]; then docker rm -f "$SRC" "$TGT" >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT

wait_ready() {
  for _ in $(seq 1 60); do
    if docker exec "$1" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1; then return 0; fi
    sleep 1
  done
  docker logs "$1" >&2
  fail "$1 did not become ready"
}

# in_container <container> <user> <password> <db> psql-args…
in_container() {
  local container="$1" user="$2" password="$3" db="$4"; shift 4
  MSYS_NO_PATHCONV=1 docker exec -i -e PGPASSWORD="$password" "$container" \
    psql -X -q -v ON_ERROR_STOP=1 -h 127.0.0.1 -U "$user" -d "$db" "$@"
}
scalar() { in_container "$@" -tA | tr -d '\r'; }

drill_started=$(date +%s)
rm -rf "$WORK"
mkdir -p "$WORK/backups"
refuse_repo_path "$WORK"

# ---------------------------------------------------------------- 1. migrations
if [ -z "${MIGRATIONS_DIR:-}" ]; then
  log "1/6 Generating and checking migration scripts (infra/ci/ef-migrations.sh)"
  MIGRATIONS_DIR="${WORK}/migrations"
  OUT_DIR="$MIGRATIONS_DIR" PG_CONTAINER="bayios-drill-migrations-check" bash "${REPO_ROOT}/infra/ci/ef-migrations.sh" >"${WORK}/ef-migrations.log" 2>&1 \
    || { tail -n 40 "${WORK}/ef-migrations.log"; fail "ef-migrations.sh failed"; }
  tail -n 1 "${WORK}/ef-migrations.log"
fi
# Apply order as recorded by ef-migrations.sh (the same order the deploy pipeline uses), else name order.
if [ -s "${MIGRATIONS_DIR}/manifest.txt" ]; then
  mapfile -t scripts < <(tr -d '\r' < "${MIGRATIONS_DIR}/manifest.txt" | sed "s#^#${MIGRATIONS_DIR}/#")
else
  mapfile -t scripts < <(ls "${MIGRATIONS_DIR}"/*.sql | sort)
fi
[ "${#scripts[@]}" -gt 0 ] || fail "no migration scripts in ${MIGRATIONS_DIR}"

# ---------------------------------------------------------------- 2. source database
log "2/6 SOURCE ${SRC} (127.0.0.1:${SRC_PORT}) with infra/docker/initdb + ${#scripts[@]} module migration scripts"
docker rm -f "$SRC" "$TGT" >/dev/null 2>&1 || true
MSYS_NO_PATHCONV=1 docker run -d --name "$SRC" -p "127.0.0.1:${SRC_PORT}:5432" \
  -e POSTGRES_DB=bayios -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD="$SUPER_PASSWORD" \
  -e BAYIOS_APP_PASSWORD="$APP_PASSWORD" -e BAYIOS_MIGRATOR_PASSWORD="$MIGRATOR_PASSWORD" \
  -v "$(native_path "${REPO_ROOT}/infra/docker/initdb"):/docker-entrypoint-initdb.d:ro" \
  "$PG_IMAGE" >/dev/null
wait_ready "$SRC"
for script in "${scripts[@]}"; do
  in_container "$SRC" bayios_migrator "$MIGRATOR_PASSWORD" bayios -f - < "$script" >/dev/null
done
echo "migrated: $(scalar "$SRC" postgres "$SUPER_PASSWORD" bayios -c "SELECT count(*) FROM pg_namespace WHERE pg_get_userbyid(nspowner) = 'bayios_migrator'") schemas owned by bayios_migrator"

# ---------------------------------------------------------------- 3. seed
log "3/6 Seeding synthetic data (infra/ops/sql/drill-seed.sql)"
in_container "$SRC" postgres "$SUPER_PASSWORD" bayios -f - < "${OPS}/sql/drill-seed.sql"

fingerprint_sql="$(cat "${OPS}/sql/drill-fingerprint.sql")"
source_fingerprint="$(scalar "$SRC" postgres "$SUPER_PASSWORD" bayios -c "$fingerprint_sql")"

# ---------------------------------------------------------------- 4. backup
log "4/6 Backup of SOURCE (infra/ops/backup.sh)"
PG_TOOLS="exec:${SRC}" PGPASSWORD="$SUPER_PASSWORD" BACKUP_DIR="${WORK}/backups" \
  BACKUP_DATABASE_URL="postgresql://postgres@127.0.0.1:5432/bayios" bash "${OPS}/backup.sh" drill

# ---------------------------------------------------------------- 5. restore into a fresh server
log "5/6 TARGET ${TGT} (127.0.0.1:${TGT_PORT}): empty postgres, restore (infra/ops/restore.sh)"
MSYS_NO_PATHCONV=1 docker run -d --name "$TGT" -p "127.0.0.1:${TGT_PORT}:5432" \
  -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD="$SUPER_PASSWORD" "$PG_IMAGE" >/dev/null
wait_ready "$TGT"
[ "$(scalar "$TGT" postgres "$SUPER_PASSWORD" postgres -c "SELECT count(*) FROM pg_roles WHERE rolname LIKE 'bayios%'")" = 0 ] \
  || fail "target is not empty"
restore_started=$(date +%s)
PG_TOOLS="exec:${TGT}" PGPASSWORD="$SUPER_PASSWORD" RESTORE_ADMIN_URL="postgresql://postgres@127.0.0.1:5432/postgres" \
  RESTORE_DATABASE="$RESTORED_DB" RESTORE_CONFIRM="$RESTORED_DB" \
  BAYIOS_APP_PASSWORD="$APP_PASSWORD" BAYIOS_MIGRATOR_PASSWORD="$MIGRATOR_PASSWORD" \
  bash "${OPS}/restore.sh" "${WORK}/backups/drill.dump"
restore_seconds=$(( $(date +%s) - restore_started ))

# ---------------------------------------------------------------- 6. drill checks (real bayios_app login on TARGET)
log "6/6 Drill checks on TARGET as bayios_app (password login, NOBYPASSRLS)"
failures=0
check() { if [ "$2" = "$3" ]; then printf '  ok   %s = %s\n' "$1" "$3"; else printf '  FAIL %s: expected %s, got %s\n' "$1" "$2" "$3"; failures=$((failures + 1)); fi; }
tenant_a="0b0a0000-0000-4000-8000-00000000000a"
tenant_b="0b0b0000-0000-4000-8000-00000000000b"
app() { scalar "$TGT" bayios_app "$APP_PASSWORD" "$RESTORED_DB" "$@"; }
in_tenant() { printf "BEGIN;\nSELECT set_config('app.tenant_id', '%s', true) \\\\g /dev/null\n%s\nCOMMIT;\n" "$1" "$2" | app -f -; }

check "login role" "bayios_app" "$(app -c 'SELECT current_user')"
check "products visible without tenant" "0" "$(app -c 'SELECT count(*) FROM catalog.products')"
check "products of tenant A seen by A" "$(scalar "$SRC" postgres "$SUPER_PASSWORD" bayios -c "SELECT count(*) FROM catalog.products WHERE tenant_id = '${tenant_a}'")" \
  "$(in_tenant "$tenant_a" 'SELECT count(*) FROM catalog.products;')"
check "products of tenant B seen by A" "0" "$(in_tenant "$tenant_a" "SELECT count(*) FROM catalog.products WHERE tenant_id = '${tenant_b}';")"
check "ledger movements of tenant B seen by B" "$(scalar "$SRC" postgres "$SUPER_PASSWORD" bayios -c "SELECT count(*) FROM inventory.inventory_movements WHERE tenant_id = '${tenant_b}'")" \
  "$(in_tenant "$tenant_b" 'SELECT count(*) FROM inventory.inventory_movements;')"
cross_write="$(printf "BEGIN;\nSELECT set_config('app.tenant_id', '%s', true) \\\\g /dev/null\nINSERT INTO catalog.brands (id, tenant_id, name, created_at, updated_at) VALUES (gen_random_uuid(), '%s', 'Drill cross-tenant', now(), now());\nROLLBACK;\n" "$tenant_a" "$tenant_b" \
  | MSYS_NO_PATHCONV=1 docker exec -i -e PGPASSWORD="$APP_PASSWORD" "$TGT" psql -X -q -v ON_ERROR_STOP=1 -h 127.0.0.1 -U bayios_app -d "$RESTORED_DB" -f - 2>&1 || true)"
check "cross-tenant INSERT (A writes B) refused by RLS" "refused" "$(grep -q 'row-level security' <<< "$cross_write" && echo refused || echo "accepted: ${cross_write}")"
check "ledger reconciliation mismatches (balances vs movements)" "0" \
  "$(scalar "$TGT" postgres "$SUPER_PASSWORD" "$RESTORED_DB" -c "SELECT count(*) FROM ($(sed 's/;[[:space:]]*$//' "${OPS}/sql/ledger-reconciliation.sql")) mismatches")"
# Control: the visibility check is not vacuous — with RLS switched off (inside a rolled-back transaction) the same
# query as bayios_app does see the other tenants' rows.
control="$(printf "BEGIN;\nALTER TABLE catalog.products DISABLE ROW LEVEL SECURITY;\nSET LOCAL ROLE bayios_app;\nSELECT count(*) FROM catalog.products;\nROLLBACK;\n" \
  | scalar "$TGT" postgres "$SUPER_PASSWORD" "$RESTORED_DB" -f - | grep -E '^[0-9]+$' || true)"
check "control: products visible to bayios_app with RLS disabled (rolled back)" \
  "$(scalar "$SRC" postgres "$SUPER_PASSWORD" bayios -c 'SELECT count(*) FROM catalog.products')" "$control"
target_fingerprint="$(scalar "$TGT" postgres "$SUPER_PASSWORD" "$RESTORED_DB" -c "$fingerprint_sql")"
check "per-tenant content fingerprint (source = target)" "$source_fingerprint" "$target_fingerprint"

echo
echo "Fingerprint: ${target_fingerprint}"
echo "Backup: $(ls -l "${WORK}/backups/drill.dump" | awk '{ print $5 }') bytes; restore ${restore_seconds} s; drill total $(( $(date +%s) - drill_started )) s"
[ "$failures" -eq 0 ] || fail "restore drill failed (${failures} check(s))"
echo "RESTORE DRILL PASSED"
