-- BayiOS Supabase bootstrap 3/3 — Custom Access Token hook: server-asserted top-level `email_verified` claim.
-- Idempotent. Skipped (with a message) on databases without Supabase Auth, e.g. the plain PostgreSQL CI rehearsal.
--
-- Why: invitation acceptance (MemberService, #129) needs to know the invitee's address is verified. Supabase puts
-- `user_metadata.email_verified` in the token, but user_metadata is writable by the user (auth.updateUser({ data })),
-- so the API only trusts it while Invitations:TrustUserMetadataEmailVerified=true. This hook adds a top-level
-- `email_verified` derived from auth.users.email_confirmed_at, which only Supabase Auth can set. With "Secure email
-- change" on, auth.users.email changes only after the new address is confirmed, so the claim always refers to `email`.
--
-- Enable after running this file (README §3): Dashboard -> Authentication -> Hooks -> "Customize Access Token (JWT)
-- Claims" -> Postgres function -> schema `bayios_auth_hooks`, function `custom_access_token`
-- (URI form: pg-functions://postgres/bayios_auth_hooks/custom_access_token). Then verify a fresh token has a
-- top-level email_verified and set Invitations__TrustUserMetadataEmailVerified=false on the API.
--
-- Design notes (Supabase "Auth Hooks" docs):
--   * Own schema instead of public: public is exposed through the Data API and Supabase's default privileges grant
--     EXECUTE on new public functions to anon/authenticated/service_role. Nothing in bayios_auth_hooks is exposed.
--   * SECURITY INVOKER (default): runs as supabase_auth_admin, which owns auth.users. No SECURITY DEFINER.
--   * Required claims are passed through unchanged; only email_verified is added/overwritten.
--   * Must finish within 2 s: one primary-key lookup.

\set ON_ERROR_STOP on

SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'supabase_auth_admin')
   AND to_regclass('auth.users') IS NOT NULL AS bayios_has_supabase_auth \gset

\if :bayios_has_supabase_auth

CREATE SCHEMA IF NOT EXISTS bayios_auth_hooks;
REVOKE ALL ON SCHEMA bayios_auth_hooks FROM PUBLIC;
GRANT USAGE ON SCHEMA bayios_auth_hooks TO supabase_auth_admin;

CREATE OR REPLACE FUNCTION bayios_auth_hooks.custom_access_token(event jsonb)
RETURNS jsonb
LANGUAGE plpgsql
STABLE
SET search_path = ''
AS $fn$
DECLARE
    claims   jsonb   := coalesce(event -> 'claims', '{}'::jsonb);
    verified boolean := false;
BEGIN
    IF coalesce((claims ->> 'is_anonymous')::boolean, false) = false AND (claims ->> 'email') IS NOT NULL THEN
        SELECT u.email_confirmed_at IS NOT NULL
               AND u.email IS NOT NULL
               AND lower(u.email) = lower(claims ->> 'email')
          INTO verified
          FROM auth.users u
         WHERE u.id = (event ->> 'user_id')::uuid;
    END IF;

    RETURN jsonb_build_object('claims', jsonb_set(claims, '{email_verified}', to_jsonb(coalesce(verified, false))));
END
$fn$;

REVOKE ALL ON FUNCTION bayios_auth_hooks.custom_access_token(jsonb) FROM PUBLIC;
DO $$
DECLARE r text;
BEGIN
    FOREACH r IN ARRAY ARRAY['anon', 'authenticated', 'service_role'] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
            EXECUTE format('REVOKE ALL ON FUNCTION bayios_auth_hooks.custom_access_token(jsonb) FROM %I', r);
            EXECUTE format('REVOKE ALL ON SCHEMA bayios_auth_hooks FROM %I', r);
        END IF;
    END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION bayios_auth_hooks.custom_access_token(jsonb) TO supabase_auth_admin;

\echo 'access-token hook bayios_auth_hooks.custom_access_token installed (enable it in Authentication -> Hooks)'

\else
\echo 'skip: no Supabase Auth in this database (supabase_auth_admin / auth.users missing); access-token hook not installed'
\endif
