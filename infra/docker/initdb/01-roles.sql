-- BayiOS database roles (ADR 0002, data-model §2.1). Local bootstrap; also executed by the API integration tests.
-- Runs once as the container superuser when the data volume is first initialised. Idempotent.
--   bayios_migrator : owns the business schemas, runs EF Core migrations. Never used by the running API.
--   bayios_app      : runtime role of the API/worker. Not an owner, NOBYPASSRLS => always subject to RLS.
-- Passwords are set by 02-role-passwords.sh from environment variables (never committed).

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'bayios_migrator') THEN
        CREATE ROLE bayios_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'bayios_app') THEN
        CREATE ROLE bayios_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT;
    END IF;

    -- The migrator creates the module schemas; the app may only connect.
    EXECUTE format('GRANT CONNECT, CREATE, TEMPORARY ON DATABASE %I TO bayios_migrator', current_database());
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO bayios_app', current_database());
END $$;

-- Business data never lives in public (data-model §1).
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
