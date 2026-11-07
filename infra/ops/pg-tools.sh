#!/usr/bin/env bash
# Shared helpers for infra/ops/backup.sh, restore.sh and restore-drill.sh (#55). Sourced, not executed.
#
# PostgreSQL client tools (pg_dump / pg_restore / psql, major version >= the server, i.e. 17) are taken from:
#   PG_TOOLS=local            the binaries on PATH (default when `pg_dump --version` reports >= 17)
#   PG_TOOLS=docker           `docker run --rm postgres:17` (PG_DOCKER_IMAGE); reaches remote hosts such as Supabase
#   PG_TOOLS=exec:<container> `docker exec` into a running postgres container (local drills; host = inside container)
# Passwords are never put on a command line: set PGPASSWORD (forwarded by name into containers) or use ~/.pgpass.

PG_DOCKER_IMAGE="${PG_DOCKER_IMAGE:-postgres:17}"

log() { printf '\n==> %s\n' "$*" >&2; }
fail() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

resolve_pg_tools() {
  if [ -n "${PG_TOOLS:-}" ]; then return; fi
  local major
  major="$(pg_dump --version 2>/dev/null | sed -n 's/.* \([0-9][0-9]*\)\..*/\1/p' | head -n 1)"
  if [ -n "$major" ] && [ "$major" -ge 17 ]; then PG_TOOLS=local; else PG_TOOLS=docker; fi
}

# pg_tool <pg_dump|pg_restore|psql> args...   (stdin/stdout are passed through, binary safe)
pg_tool() {
  resolve_pg_tools
  case "$PG_TOOLS" in
    local) "$@" ;;
    docker) MSYS_NO_PATHCONV=1 docker run --rm -i -e PGPASSWORD -e PGSSLMODE "$PG_DOCKER_IMAGE" "$@" ;;
    exec:*) MSYS_NO_PATHCONV=1 docker exec -i -e PGPASSWORD -e PGSSLMODE "${PG_TOOLS#exec:}" "$@" ;;
    *) fail "PG_TOOLS must be local, docker or exec:<container> (got '${PG_TOOLS}')" ;;
  esac
}

# psql_at <url> <sql>  — one unaligned, tuples-only result, ON_ERROR_STOP.
# stdin is /dev/null: inside `while read` loops docker exec -i would otherwise swallow the loop's input.
psql_at() { pg_tool psql -X -q -v ON_ERROR_STOP=1 -tA -d "$1" -c "$2" < /dev/null | tr -d '\r'; }

# Refuses an output directory inside the git work tree: backups contain tenant data and must never be committed.
refuse_repo_path() {
  local dir="$1" top
  top="$(git -C "$dir" rev-parse --show-toplevel 2>/dev/null || true)"
  [ -z "$top" ] || fail "${dir} is inside the git repository ${top}. Backups hold tenant data: choose a directory outside the repo."
}

sha256_of() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

# Prints "<schema>.<table>\t<rows>" for every table of the given comma-separated schemas (needs a role that sees all rows).
table_counts_sql() {
  cat <<SQL
SELECT format('%s.%s', n.nspname, c.relname) || E'\t' ||
       (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I.%I', n.nspname, c.relname), false, true, '')))[1]::text
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('r', 'p') AND n.nspname = ANY (string_to_array('$1', ','))
ORDER BY 1;
SQL
}

# Prints "<schema>.<table>\t<rls enabled>\t<rls forced>" for every table of the given schemas.
rls_flags_sql() {
  cat <<SQL
SELECT format('%s.%s', n.nspname, c.relname) || E'\t' || c.relrowsecurity || E'\t' || c.relforcerowsecurity
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('r', 'p') AND n.nspname = ANY (string_to_array('$1', ','))
ORDER BY 1;
SQL
}
