-- BayiOS database posture check (issue #76; threat-model R1/R2). Read-only; fails (RAISE EXCEPTION) on any violation.
--
-- Runs as any role that can connect (catalogs are readable): bootstrap.sh runs it as postgres after the bootstrap,
-- the deploy pipeline (infra/deploy/apply-migrations.sh) runs it as bayios_migrator right after the migrations,
-- and infra/supabase/bootstrap/test.sh runs it in CI. Before the first migration the business-schema checks are vacuous.
--
-- Business schemas = schemas owned by bayios_migrator (every module schema is created by the migrations).

\set ON_ERROR_STOP on

DO $$
DECLARE
    app       pg_roles%ROWTYPE;
    migrator  pg_roles%ROWTYPE;
    bad       text;
    client    text;
    hook      oid;
BEGIN
    -- 1. Runtime role: login, no elevated attributes, no inherited memberships, owns nothing.
    SELECT * INTO app FROM pg_roles WHERE rolname = 'bayios_app';
    IF NOT FOUND THEN RAISE EXCEPTION 'verify: role bayios_app missing'; END IF;
    IF app.rolsuper OR app.rolbypassrls OR app.rolcreaterole OR app.rolcreatedb OR app.rolreplication
       OR app.rolinherit OR NOT app.rolcanlogin THEN
        RAISE EXCEPTION 'verify: bayios_app must be LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEROLE NOCREATEDB NOREPLICATION NOINHERIT';
    END IF;
    SELECT string_agg(g.rolname, ', ') INTO bad
      FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid
     WHERE m.member = app.oid;
    IF bad IS NOT NULL THEN RAISE EXCEPTION 'verify: bayios_app must not be a member of any role (is member of: %)', bad; END IF;
    SELECT string_agg(x, ', ') INTO bad FROM (
        SELECT 'schema ' || nspname AS x FROM pg_namespace WHERE nspowner = app.oid
        UNION ALL SELECT 'relation ' || c.oid::regclass::text FROM pg_class c WHERE c.relowner = app.oid
        UNION ALL SELECT 'function ' || p.oid::regprocedure::text FROM pg_proc p WHERE p.proowner = app.oid
    ) owned;
    IF bad IS NOT NULL THEN RAISE EXCEPTION 'verify: bayios_app must own nothing (owns: %)', bad; END IF;

    -- 2. Migration role: no superuser / RLS bypass.
    SELECT * INTO migrator FROM pg_roles WHERE rolname = 'bayios_migrator';
    IF NOT FOUND THEN RAISE EXCEPTION 'verify: role bayios_migrator missing'; END IF;
    IF migrator.rolsuper OR migrator.rolbypassrls OR migrator.rolcreaterole OR migrator.rolreplication THEN
        RAISE EXCEPTION 'verify: bayios_migrator must be NOSUPERUSER NOBYPASSRLS NOCREATEROLE NOREPLICATION';
    END IF;

    -- 3. pg_trgm present (search indexes).
    IF NOT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm') THEN
        RAISE EXCEPTION 'verify: extension pg_trgm missing (run 02-extensions.sql)';
    END IF;

    -- 4. Business schemas: no PUBLIC / Supabase client role access; every table under forced RLS.
    SELECT string_agg(n.nspname, ', ') INTO bad
      FROM pg_namespace n
     WHERE n.nspowner = migrator.oid AND has_schema_privilege('public', n.oid, 'USAGE');
    IF bad IS NOT NULL THEN RAISE EXCEPTION 'verify: PUBLIC has USAGE on business schema(s): %', bad; END IF;

    FOREACH client IN ARRAY ARRAY['anon', 'authenticated'] LOOP
        CONTINUE WHEN NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = client);
        SELECT string_agg(n.nspname, ', ') INTO bad
          FROM pg_namespace n
         WHERE n.nspowner = migrator.oid AND has_schema_privilege(client, n.oid, 'USAGE');
        IF bad IS NOT NULL THEN RAISE EXCEPTION 'verify: % has USAGE on business schema(s): %', client, bad; END IF;
        SELECT string_agg(c.oid::regclass::text, ', ') INTO bad
          FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
         WHERE n.nspowner = migrator.oid AND c.relkind IN ('r', 'p', 'v', 'm')
           AND has_table_privilege(client, c.oid, 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER');
        IF bad IS NOT NULL THEN RAISE EXCEPTION 'verify: % has privileges on business table(s): %', client, bad; END IF;
    END LOOP;

    SELECT string_agg(c.oid::regclass::text, ', ') INTO bad
      FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE n.nspowner = migrator.oid AND c.relkind IN ('r', 'p')
       AND c.relname <> '__ef_migrations_history'
       -- Accepted exceptions (data-model §2.3, migration 20261104210707_AuditJobsOutbox): the multi-tenant worker
       -- queue re-establishes the tenant per job. Adding a table here needs the same documented decision.
       AND c.oid::regclass::text NOT IN ('platform.jobs', 'platform.outbox_messages')
       AND NOT (c.relrowsecurity AND c.relforcerowsecurity);
    IF bad IS NOT NULL THEN RAISE EXCEPTION 'verify: tables without ENABLE + FORCE ROW LEVEL SECURITY: %', bad; END IF;

    -- 5. Access-token hook (Supabase only): callable by Supabase Auth and nobody else. Looked up by OID from the
    --    catalog, because the checking role (e.g. bayios_migrator) has no USAGE on bayios_auth_hooks by design.
    SELECT p.oid INTO hook FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
     WHERE n.nspname = 'bayios_auth_hooks' AND p.proname = 'custom_access_token';
    IF hook IS NOT NULL THEN
        IF NOT has_function_privilege('supabase_auth_admin', hook, 'EXECUTE')
           OR NOT has_schema_privilege('supabase_auth_admin', 'bayios_auth_hooks', 'USAGE') THEN
            RAISE EXCEPTION 'verify: supabase_auth_admin cannot execute the access-token hook';
        END IF;
        FOREACH client IN ARRAY ARRAY['public', 'anon', 'authenticated', 'service_role', 'bayios_app'] LOOP
            CONTINUE WHEN client <> 'public' AND NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = client);
            IF has_function_privilege(client, hook, 'EXECUTE') OR has_schema_privilege(client, 'bayios_auth_hooks', 'USAGE') THEN
                RAISE EXCEPTION 'verify: % can execute the access-token hook', client;
            END IF;
        END LOOP;
    END IF;

    RAISE NOTICE 'verify: OK (% business schema(s), % table(s) checked)',
        (SELECT count(*) FROM pg_namespace WHERE nspowner = migrator.oid),
        (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
          WHERE n.nspowner = migrator.oid AND c.relkind IN ('r', 'p') AND c.relname <> '__ef_migrations_history');
END $$;
