#!/usr/bin/env bash
# First start only: extensions in the karry database, the Karry roles (deploy/database/roles.sql) and Keycloak's database.
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username postgres --dbname karry \
  -v owner_password="$KARRY_OWNER_PASSWORD" -v app_password="$KARRY_APP_PASSWORD" -v keycloak_password="$KEYCLOAK_DB_PASSWORD" <<'SQL'
\i /karry/database/extensions.sql

SET karry.owner_password = :'owner_password';
SET karry.app_password = :'app_password';
\i /karry/database/roles.sql

CREATE ROLE keycloak LOGIN PASSWORD :'keycloak_password' NOSUPERUSER NOCREATEDB NOCREATEROLE;
CREATE DATABASE keycloak OWNER keycloak;
SQL
