-- BayiOS Supabase bootstrap 2/3 — extensions (issue #76). Idempotent.
--
-- pg_trgm backs the "contains" search indexes (Catalog, Customers migrations). On Supabase extensions live in the
-- `extensions` schema and are created through supautils by the `postgres` role; bayios_migrator cannot create them
-- there (it has no privileges on `extensions`). The migrations create pg_trgm only when it is missing and resolve the
-- operator class from wherever it is installed, so pre-creating it here is all they need.
--
-- bayios_migrator gets USAGE on `extensions` so that `CREATE INDEX ... USING gin (name extensions.gin_trgm_ops)` can
-- resolve the operator class. bayios_app gets nothing: the API only uses ILIKE, which the planner serves from the
-- trigram index without any privilege on the extension's schema or functions (verified by test.sh). If a feature ever
-- calls trigram functions (similarity(), %, word_similarity()) directly, grant USAGE on the schema and EXECUTE on
-- exactly those functions to bayios_app in a reviewed change.

\set ON_ERROR_STOP on

CREATE SCHEMA IF NOT EXISTS extensions;

DO $$
DECLARE
    s text;
BEGIN
    SELECT n.nspname INTO s FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = 'pg_trgm';
    IF s IS NULL THEN
        CREATE EXTENSION pg_trgm WITH SCHEMA extensions;
        RAISE NOTICE 'created extension pg_trgm in schema extensions';
    ELSIF s <> 'extensions' THEN
        -- Do not move it: existing indexes reference its operator classes. Migrations handle any schema.
        RAISE WARNING 'pg_trgm is already installed in schema % (expected extensions); leaving it there', s;
    END IF;
END $$;

GRANT USAGE ON SCHEMA extensions TO bayios_migrator;
