#!/bin/sh
# Sets login passwords for the roles created by 01-roles.sql from environment variables (see compose.yml).
set -eu

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
    -v app_password="${BAYIOS_APP_PASSWORD:?BAYIOS_APP_PASSWORD is required}" \
    -v migrator_password="${BAYIOS_MIGRATOR_PASSWORD:?BAYIOS_MIGRATOR_PASSWORD is required}" <<'SQL'
ALTER ROLE bayios_app PASSWORD :'app_password';
ALTER ROLE bayios_migrator PASSWORD :'migrator_password';
SQL
