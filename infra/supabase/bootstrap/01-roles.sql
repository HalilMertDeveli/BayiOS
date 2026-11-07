-- BayiOS Supabase bootstrap 1/3 — database roles (ADR 0002, data-model §2.1, issue #76).
-- Supabase counterpart of infra/docker/initdb/01-roles.sql. Idempotent: safe to run any number of times.
--
-- Run as the project's `postgres` role (Supabase: NOT a superuser, but CREATEROLE + database owner) through
-- infra/supabase/bootstrap/bootstrap.sh. Also runs unchanged on plain PostgreSQL 17 as a superuser (CI rehearsal).
--
--   bayios_migrator : owns the business schemas, runs the EF Core migration scripts in the deploy pipeline only.
--   bayios_app      : runtime role of the API/worker. Owns nothing, NOBYPASSRLS + NOINHERIT => always subject to RLS.
--
-- Passwords are NOT set here. Supabase logs DDL (log_statement = ddl), so a plain-text PASSWORD '...' would land in
-- the database logs. bootstrap.sh sets them with psql's \password, which sends only a SCRAM-SHA-256 verifier.
--
-- Existing roles are never silently "repaired": if a role exists with dangerous attributes the script fails and a
-- human must look (fail closed). Attribute changes such as NOSUPERUSER need a superuser on Supabase anyway.

\set ON_ERROR_STOP on

DO $$
DECLARE
    r record;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'bayios_migrator') THEN
        CREATE ROLE bayios_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
        RAISE NOTICE 'created role bayios_migrator';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'bayios_app') THEN
        CREATE ROLE bayios_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT;
        RAISE NOTICE 'created role bayios_app';
    END IF;

    FOR r IN SELECT * FROM pg_roles WHERE rolname IN ('bayios_app', 'bayios_migrator') LOOP
        IF r.rolsuper OR r.rolbypassrls OR r.rolcreaterole OR r.rolcreatedb OR r.rolreplication OR NOT r.rolcanlogin THEN
            RAISE EXCEPTION 'role % exists with unexpected attributes (super=%, bypassrls=%, createrole=%, createdb=%, replication=%, login=%); fix it manually',
                r.rolname, r.rolsuper, r.rolbypassrls, r.rolcreaterole, r.rolcreatedb, r.rolreplication, r.rolcanlogin;
        END IF;
    END LOOP;

    IF (SELECT rolinherit FROM pg_roles WHERE rolname = 'bayios_app') THEN
        RAISE EXCEPTION 'role bayios_app must be NOINHERIT';
    END IF;

    -- The migrator creates the module schemas; the app may only connect.
    EXECUTE format('GRANT CONNECT, CREATE, TEMPORARY ON DATABASE %I TO bayios_migrator', current_database());
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO bayios_app', current_database());
END $$;

-- Business data never lives in public (data-model §1). On Supabase PUBLIC already has no CREATE here; this keeps
-- parity with local. Supabase's own grants on public (anon/authenticated/service_role) are left untouched.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
