#!/bin/sh
# Runs once, when the PostgreSQL data directory is first initialised (docker-entrypoint-initdb.d). The integration tests
# run this exact script, so the least-privilege setup is tested, not assumed. Two databases stand for two systems:
#   dsar    the workflow store.  dsar_migrator owns the schemas and runs migrations; dsar_app is what the service uses
#           (no DELETE anywhere, INSERT/SELECT only on the audit chain).
#   clinic  the synthetic source system.  clinic_owner owns and seeds it; dsar_finder may only SELECT (discovery,
#           contact lookup, access export); dsar_eraser may SELECT/UPDATE/DELETE and is used only to execute an
#           approved plan. Grants on the tables are issued by clinic_owner after loading (ClinicDatabase.Grants).
set -eu

: "${MIGRATOR_PASSWORD:?MIGRATOR_PASSWORD must be set}"
: "${APP_PASSWORD:?APP_PASSWORD must be set}"
: "${CLINIC_OWNER_PASSWORD:?CLINIC_OWNER_PASSWORD must be set}"
: "${FINDER_PASSWORD:?FINDER_PASSWORD must be set}"
: "${ERASER_PASSWORD:?ERASER_PASSWORD must be set}"

psql -v ON_ERROR_STOP=1 \
     -v migrator_password="$MIGRATOR_PASSWORD" -v app_password="$APP_PASSWORD" \
     -v owner_password="$CLINIC_OWNER_PASSWORD" -v finder_password="$FINDER_PASSWORD" -v eraser_password="$ERASER_PASSWORD" \
     -v db="$POSTGRES_DB" \
     --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
CREATE ROLE dsar_migrator LOGIN PASSWORD :'migrator_password';
CREATE ROLE dsar_app LOGIN PASSWORD :'app_password';
CREATE ROLE clinic_owner LOGIN PASSWORD :'owner_password';
CREATE ROLE dsar_finder LOGIN PASSWORD :'finder_password';
CREATE ROLE dsar_eraser LOGIN PASSWORD :'eraser_password';

REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db" TO dsar_migrator, dsar_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
CREATE SCHEMA dsar AUTHORIZATION dsar_migrator;
CREATE SCHEMA dsar_admin AUTHORIZATION dsar_migrator;

CREATE DATABASE clinic OWNER clinic_owner;
SQL

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname clinic <<'SQL'
REVOKE ALL ON DATABASE clinic FROM PUBLIC;
GRANT CONNECT ON DATABASE clinic TO clinic_owner, dsar_finder, dsar_eraser;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
CREATE SCHEMA clinic AUTHORIZATION clinic_owner;
-- Trigram indexes are what the index advisor proposes for free-text search; the extension needs a superuser.
CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public;
SQL
