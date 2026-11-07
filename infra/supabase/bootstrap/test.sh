#!/usr/bin/env bash
# Rehearsal of the Supabase bootstrap + deploy-time migration step against THROWAWAY containers (issue #70/#76).
# Used by .github/workflows/ci-db-bootstrap.yml and runnable locally from the repo root (bash + Docker):
#
#   SCRIPTS_DIR=<dir with EF scripts + manifest.txt from infra/ci/ef-migrations.sh> \
#     bash infra/supabase/bootstrap/test.sh postgres|supabase
#
#   postgres : plain postgres:17, bootstrap run as a superuser (hook step must skip cleanly).
#   supabase : the Supabase Postgres image used by the Supabase CLI (postgres role is NOT a superuser there) plus
#              Supabase Auth (GoTrue), which migrates the auth schema; the access-token hook is tested end to end.
#
# Every container lives on a private Docker network; only GoTrue is published, on a random 127.0.0.1 port. No host
# ports are fixed, so a developer's running stacks are not touched. Passwords are throwaway local-only values.
#
# Steps: bootstrap.sh x2 (idempotent) -> set role passwords -> apply-migrations.sh x2 as bayios_migrator ->
#        verify.sql as postgres -> bayios_app RLS + trigram-index smoke -> (supabase) access-token hook checks.
set -euo pipefail

FLAVOR="${1:-postgres}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
: "${SCRIPTS_DIR:?SCRIPTS_DIR (EF scripts + manifest.txt) is required}"
PG_IMAGE="${PG_IMAGE:-postgres:17}"
SUPABASE_PG_IMAGE="${SUPABASE_PG_IMAGE:-public.ecr.aws/supabase/postgres:17.11.0.006}"
GOTRUE_IMAGE="${GOTRUE_IMAGE:-public.ecr.aws/supabase/gotrue:v2.197.0}"
PREFIX="${CONTAINER_PREFIX:-bayios-bootstrap-test}"
NET="${PREFIX}-net"
DB="${PREFIX}-db"
AUTH="${PREFIX}-auth"
PW="test-local-only"

log() { printf '\n==> %s\n' "$*"; }
fail() { printf '::error::%s\n' "$*" >&2; exit 1; }
native_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

cleanup() {
  docker rm -f "$AUTH" "$DB" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
}
trap cleanup EXIT
cleanup
docker network create "$NET" >/dev/null

case "$FLAVOR" in
  postgres) image="$PG_IMAGE" ;;
  supabase) image="$SUPABASE_PG_IMAGE" ;;
  *) fail "flavor must be postgres or supabase" ;;
esac

log "Starting ${image} (${FLAVOR})"
MSYS_NO_PATHCONV=1 docker run -d --name "$DB" --network "$NET" -e POSTGRES_PASSWORD="$PW" "$image" >/dev/null
ready=0
for _ in $(seq 1 90); do
  if docker exec "$DB" pg_isready -h 127.0.0.1 -U postgres -d postgres >/dev/null 2>&1; then ready=1; break; fi
  sleep 2
done
[ "$ready" = 1 ] || { docker logs "$DB" | tail -50; fail "database did not become ready"; }
sleep 3 # the Supabase image restarts once after its init scripts

# psql inside a client container on the same network (no psql needed on the host). Args: user, then psql args.
client() {
  local user="$1"; shift
  MSYS_NO_PATHCONV=1 docker run --rm -i --network "$NET" \
    -v "$(native_path "$REPO_ROOT"):/repo:ro" -v "$(native_path "$SCRIPTS_DIR"):/scripts:ro" \
    -e PGPASSWORD="$PW" "$PG_IMAGE" psql -h "$DB" -U "$user" -d postgres -X -v ON_ERROR_STOP=1 -q "$@"
}
for _ in $(seq 1 30); do client postgres -tAc 'select 1' >/dev/null 2>&1 && break; sleep 2; done

if [ "$FLAVOR" = supabase ]; then
  log "Supabase role check: postgres must not be a superuser here"
  [ "$(client postgres -tAc "select rolsuper from pg_roles where rolname = current_user")" = f ] \
    || fail "expected a non-superuser postgres role in the Supabase image"

  log "Starting Supabase Auth (GoTrue) to migrate the auth schema, with the access-token hook enabled"
  client supabase_admin -c "ALTER ROLE supabase_auth_admin PASSWORD '${PW}'"
  MSYS_NO_PATHCONV=1 docker run -d --name "$AUTH" --network "$NET" -p 127.0.0.1::9999 \
    -e GOTRUE_API_HOST=0.0.0.0 -e GOTRUE_API_PORT=9999 -e API_EXTERNAL_URL=http://127.0.0.1:9999 \
    -e GOTRUE_DB_DRIVER=postgres \
    -e GOTRUE_DB_DATABASE_URL="postgres://supabase_auth_admin:${PW}@${DB}:5432/postgres?sslmode=disable" \
    -e GOTRUE_SITE_URL=http://localhost:3200 -e GOTRUE_URI_ALLOW_LIST=http://localhost:3200/auth/callback \
    -e GOTRUE_JWT_SECRET=test-local-only-jwt-secret-0123456789abcdef -e GOTRUE_JWT_EXP=3600 \
    -e GOTRUE_JWT_AUD=authenticated -e GOTRUE_JWT_DEFAULT_GROUP_NAME=authenticated -e GOTRUE_JWT_ADMIN_ROLES=service_role \
    -e GOTRUE_DISABLE_SIGNUP=false -e GOTRUE_EXTERNAL_EMAIL_ENABLED=true -e GOTRUE_MAILER_AUTOCONFIRM=true \
    -e GOTRUE_EXTERNAL_ANONYMOUS_USERS_ENABLED=true -e GOTRUE_PASSWORD_MIN_LENGTH=8 \
    -e GOTRUE_HOOK_CUSTOM_ACCESS_TOKEN_ENABLED=true \
    -e GOTRUE_HOOK_CUSTOM_ACCESS_TOKEN_URI=pg-functions://postgres/bayios_auth_hooks/custom_access_token \
    "$GOTRUE_IMAGE" >/dev/null
  auth_port="$(docker port "$AUTH" 9999/tcp | head -n 1 | sed 's/.*://')"
  AUTH_URL="http://127.0.0.1:${auth_port}"
  ok=0
  for _ in $(seq 1 60); do curl -fsS "${AUTH_URL}/health" >/dev/null 2>&1 && { ok=1; break; }; sleep 2; done
  [ "$ok" = 1 ] || { docker logs "$AUTH" | tail -50; fail "GoTrue did not become healthy"; }
fi

for pass in 1 2; do
  log "bootstrap.sh pass ${pass} (as postgres)"
  MSYS_NO_PATHCONV=1 docker run --rm -i --network "$NET" -v "$(native_path "$REPO_ROOT"):/repo:ro" \
    -e SUPABASE_DB_URL="postgresql://postgres:${PW}@${DB}:5432/postgres?sslmode=disable" \
    -e ALLOW_INSECURE_DB_CONNECTION=1 "$PG_IMAGE" bash /repo/infra/supabase/bootstrap/bootstrap.sh
done

log "Setting throwaway role passwords (test only; real projects use bootstrap.sh --set-passwords)"
client postgres -c "ALTER ROLE bayios_migrator PASSWORD '${PW}'" -c "ALTER ROLE bayios_app PASSWORD '${PW}'"

for pass in 1 2; do
  log "apply-migrations.sh pass ${pass} (as bayios_migrator)"
  MSYS_NO_PATHCONV=1 docker run --rm -i --network "$NET" \
    -v "$(native_path "$REPO_ROOT"):/repo:ro" -v "$(native_path "$SCRIPTS_DIR"):/scripts:ro" \
    -e MIGRATOR_URL="postgresql://bayios_migrator:${PW}@${DB}:5432/postgres?sslmode=disable" \
    -e SCRIPTS_DIR=/scripts -e ALLOW_INSECURE_DB_CONNECTION=1 "$PG_IMAGE" bash /repo/infra/deploy/apply-migrations.sh
done

log "Guard rail: apply-migrations.sh refuses the postgres role"
if MSYS_NO_PATHCONV=1 docker run --rm -i --network "$NET" -v "$(native_path "$REPO_ROOT"):/repo:ro" \
    -v "$(native_path "$SCRIPTS_DIR"):/scripts:ro" \
    -e MIGRATOR_URL="postgresql://postgres:${PW}@${DB}:5432/postgres?sslmode=require" -e SCRIPTS_DIR=/scripts \
    "$PG_IMAGE" bash /repo/infra/deploy/apply-migrations.sh >/dev/null 2>&1; then
  fail "apply-migrations.sh accepted the postgres role"
fi
echo "refused (expected)"

log "Guard rail: apply-migrations.sh refuses a URL without sslmode=require"
if MSYS_NO_PATHCONV=1 docker run --rm -i --network "$NET" -v "$(native_path "$REPO_ROOT"):/repo:ro" \
    -v "$(native_path "$SCRIPTS_DIR"):/scripts:ro" \
    -e MIGRATOR_URL="postgresql://bayios_migrator:${PW}@${DB}:5432/postgres" -e SCRIPTS_DIR=/scripts \
    "$PG_IMAGE" bash /repo/infra/deploy/apply-migrations.sh >/dev/null 2>&1; then
  fail "apply-migrations.sh accepted a URL without TLS"
fi
echo "refused (expected)"

log "verify.sql as postgres"
client postgres -f /repo/infra/supabase/bootstrap/verify.sql

log "bayios_app smoke: RLS fail-closed on a business table"
[ "$(client bayios_app -tAc "select count(*) from catalog.products")" = 0 ] || fail "bayios_app saw rows without tenant context"
echo "ok: no tenant context => 0 rows"

log "bayios_app smoke: ILIKE served by a pg_trgm GIN index without privileges on the extension schema"
client postgres <<'SQL'
\set ON_ERROR_STOP on
DROP SCHEMA IF EXISTS bayios_bootstrap_test CASCADE;
CREATE SCHEMA bayios_bootstrap_test;
CREATE TABLE bayios_bootstrap_test.t (id int PRIMARY KEY, name text NOT NULL);
INSERT INTO bayios_bootstrap_test.t SELECT g, 'urun vida ' || g FROM generate_series(1, 200) g;
DO $$
DECLARE s text;
BEGIN
    SELECT n.nspname INTO s FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = 'pg_trgm';
    EXECUTE format('CREATE INDEX t_name_trgm ON bayios_bootstrap_test.t USING gin (name %I.gin_trgm_ops)', s);
END $$;
ANALYZE bayios_bootstrap_test.t;
GRANT USAGE ON SCHEMA bayios_bootstrap_test TO bayios_app;
GRANT SELECT ON bayios_bootstrap_test.t TO bayios_app;
SQL
client bayios_app <<'SQL'
\set ON_ERROR_STOP on
BEGIN;
SET LOCAL enable_seqscan = off;
DO $$
DECLARE plan text := ''; line text; n int;
BEGIN
    IF has_schema_privilege('extensions', 'USAGE') THEN RAISE EXCEPTION 'bayios_app unexpectedly has USAGE on extensions'; END IF;
    FOR line IN EXECUTE 'EXPLAIN SELECT id FROM bayios_bootstrap_test.t WHERE name ILIKE ''%vida 1%''' LOOP
        plan := plan || ' ' || trim(line);
    END LOOP;
    IF plan NOT LIKE '%t_name_trgm%' THEN RAISE EXCEPTION 'trigram index not used: %', plan; END IF;
    SELECT count(*) INTO n FROM bayios_bootstrap_test.t WHERE name ILIKE '%vida 1%';
    IF n = 0 THEN RAISE EXCEPTION 'no rows'; END IF;
    RAISE NOTICE 'ok: % rows via % (no USAGE on the extension schema)', n, plan;
END $$;
COMMIT;
SQL
client postgres -c "DROP SCHEMA bayios_bootstrap_test CASCADE"
if client bayios_app -c "create table public.x(id int)" >/dev/null 2>&1; then fail "bayios_app could create a table in public"; fi
echo "ok: bayios_app cannot create objects in public"

if [ "$FLAVOR" = postgres ]; then
  log "OK (${FLAVOR}): bootstrap idempotent, migrations applied twice as bayios_migrator, posture verified"
  exit 0
fi

# ---------------------------------------------------------------- Supabase only: access-token hook
log "Hook privileges: anon/authenticated cannot call it"
for role in anon authenticated; do
  if client supabase_admin -c "SET ROLE ${role}; SELECT bayios_auth_hooks.custom_access_token('{}'::jsonb)" >/dev/null 2>&1; then
    fail "${role} can execute the access-token hook"
  fi
done
echo "ok: denied for anon and authenticated"

jwt_claim() { # $1 = JWT, $2 = TOP-LEVEL claim name -> prints its JSON value (decoded with jsonb, not grep, so a
  # nested user_metadata.email_verified can never be mistaken for the top-level claim)
  local payload; payload="$(printf '%s' "$1" | cut -d. -f2)"
  case "$payload" in *[!A-Za-z0-9_-]*) fail "unexpected JWT payload characters" ;; esac
  case "$2" in *[!a-z_]*) fail "bad claim name" ;; esac
  client postgres -tAc "select (convert_from(decode(translate('${payload}', '-_', '+/')
      || repeat('=', (4 - length('${payload}') % 4) % 4), 'base64'), 'UTF8')::jsonb) -> '$2'"
}

log "Hook end to end: confirmed e-mail user gets top-level email_verified=true"
signup="$(curl -fsS -X POST "${AUTH_URL}/signup" -H 'Content-Type: application/json' \
  -d '{"email":"hook-test@example.com","password":"Test-local-only-1"}')"
token="$(printf '%s' "$signup" | grep -oE '"access_token":"[^"]+"' | cut -d'"' -f4)"
[ -n "$token" ] || fail "signup returned no access token: ${signup}"
[ "$(jwt_claim "$token" email_verified)" = true ] || fail "email_verified claim is not true for a confirmed user"
echo "ok: email_verified=true"

log "Hook end to end: anonymous user gets email_verified=false"
anon="$(curl -fsS -X POST "${AUTH_URL}/signup" -H 'Content-Type: application/json' -d '{}')"
anon_token="$(printf '%s' "$anon" | grep -oE '"access_token":"[^"]+"' | cut -d'"' -f4)"
[ -n "$anon_token" ] || fail "anonymous signup returned no access token: ${anon}"
[ "$(jwt_claim "$anon_token" email_verified)" = false ] || fail "email_verified claim is not false for an anonymous user"
echo "ok: email_verified=false"

log "Hook unit checks: unconfirmed address, mismatching e-mail claim"
client supabase_admin <<'SQL'
\set ON_ERROR_STOP on
UPDATE auth.users SET email_confirmed_at = NULL WHERE email = 'hook-test@example.com';
SET ROLE supabase_auth_admin;
DO $$
DECLARE uid uuid; r jsonb;
BEGIN
    SELECT id INTO uid FROM auth.users WHERE email = 'hook-test@example.com';
    r := bayios_auth_hooks.custom_access_token(jsonb_build_object('user_id', uid,
         'claims', jsonb_build_object('sub', uid, 'email', 'hook-test@example.com', 'is_anonymous', false, 'role', 'authenticated')));
    IF (r -> 'claims' ->> 'email_verified')::boolean THEN RAISE EXCEPTION 'unconfirmed user marked verified: %', r; END IF;
    IF r -> 'claims' ->> 'role' IS DISTINCT FROM 'authenticated' THEN RAISE EXCEPTION 'required claim lost: %', r; END IF;
    RAISE NOTICE 'ok: unconfirmed address => email_verified=false, other claims kept';
END $$;
RESET ROLE;
UPDATE auth.users SET email_confirmed_at = now() WHERE email = 'hook-test@example.com';
SET ROLE supabase_auth_admin;
DO $$
DECLARE uid uuid; r jsonb;
BEGIN
    SELECT id INTO uid FROM auth.users WHERE email = 'hook-test@example.com';
    r := bayios_auth_hooks.custom_access_token(jsonb_build_object('user_id', uid,
         'claims', jsonb_build_object('sub', uid, 'email', 'someone-else@example.com', 'is_anonymous', false)));
    IF (r -> 'claims' ->> 'email_verified')::boolean THEN RAISE EXCEPTION 'mismatching e-mail marked verified: %', r; END IF;
    RAISE NOTICE 'ok: e-mail claim not matching auth.users => email_verified=false';
END $$;
SQL

log "verify.sql again (hook privileges included)"
client postgres -f /repo/infra/supabase/bootstrap/verify.sql

log "OK (${FLAVOR}): bootstrap idempotent, migrations applied twice as bayios_migrator, posture verified, hook works"
