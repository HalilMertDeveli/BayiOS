#!/usr/bin/env bash
# BayiOS Supabase bootstrap runner (issue #76). Run ONCE per Supabase project by the product owner (or re-run any time:
# every step is idempotent). Not run by CI against hosted projects; CI rehearses it in test.sh.
#
#   SUPABASE_DB_URL='postgresql://postgres.<project-ref>:<db-password>@aws-0-<region>.pooler.supabase.com:5432/postgres?sslmode=require' \
#     bash infra/supabase/bootstrap/bootstrap.sh [--set-passwords]
#
# SUPABASE_DB_URL: the `postgres` role, Session pooler (port 5432) from Dashboard -> Connect. Never committed, never
#   printed, not stored in GitHub (the deploy pipeline only ever gets the bayios_migrator URL).
# --set-passwords: afterwards prompts (twice each) for the bayios_migrator and bayios_app passwords via psql \password,
#   which sends only a SCRAM-SHA-256 verifier — the plain password never reaches Supabase's DDL log.
#   Generate them first, e.g. `openssl rand -base64 32 | tr -d '/+='` (URL-safe, so no percent-encoding in URIs).
#
# Steps: 01-roles.sql -> 02-extensions.sql -> 03-access-token-hook.sql -> verify.sql.
# psql: uses a local psql if present, otherwise the postgres:17 Docker image (PSQL_IMAGE).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PSQL_IMAGE="${PSQL_IMAGE:-postgres:17}"
set_passwords=0
for arg in "$@"; do
  case "$arg" in
    --set-passwords) set_passwords=1 ;;
    -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

fail() { printf 'error: %s\n' "$*" >&2; exit 1; }
[ -n "${SUPABASE_DB_URL:-}" ] || fail "SUPABASE_DB_URL is not set (postgres role, session pooler URI)"
case "$SUPABASE_DB_URL" in postgres://*|postgresql://*) ;; *) fail "SUPABASE_DB_URL must be a postgresql:// URI" ;; esac
if [ "${ALLOW_INSECURE_DB_CONNECTION:-0}" != 1 ]; then
  printf '%s' "$SUPABASE_DB_URL" | grep -Eq '[?&]sslmode=(require|verify-ca|verify-full)(&|$)' \
    || fail "SUPABASE_DB_URL must contain sslmode=require"
fi

native_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

# psql wrapper: local client, or a throwaway container (stdin/tty passed through, URL via env, never on the command line).
run_psql() {
  if command -v psql >/dev/null 2>&1; then
    psql "$SUPABASE_DB_URL" -X -v ON_ERROR_STOP=1 "$@"
  else
    local tty=(-i); [ -t 0 ] && tty=(-it)
    MSYS_NO_PATHCONV=1 docker run --rm "${tty[@]}" -e SUPABASE_DB_URL \
      -v "$(native_path "$HERE"):/bootstrap:ro" -w /bootstrap \
      "$PSQL_IMAGE" sh -c 'exec psql "$SUPABASE_DB_URL" -X -v ON_ERROR_STOP=1 "$@"' psql "$@"
  fi
}

if command -v psql >/dev/null 2>&1; then dir="$HERE"; else dir=/bootstrap; fi

who="$(run_psql -tAc 'select current_user' | tr -d '[:space:]')"
echo "connected as: ${who}"
[ "$who" = postgres ] || [ "${ALLOW_NON_POSTGRES_ROLE:-0}" = 1 ] || fail "expected to run as the postgres role, got '${who}'"

for f in 01-roles.sql 02-extensions.sql 03-access-token-hook.sql verify.sql; do
  echo "==> ${f}"
  run_psql -q -f "${dir}/${f}"
done

if [ "$set_passwords" = 1 ]; then
  echo "==> password for bayios_migrator (deploy pipeline only)"
  run_psql -c '\password bayios_migrator'
  echo "==> password for bayios_app (API runtime)"
  run_psql -c '\password bayios_app'
fi

echo "bootstrap OK. Next: README.md §3 (Auth settings, enable the access-token hook)."
