#!/usr/bin/env bash
# First start only: extensions in the karry database, the runtime role, and Keycloak's database.
# Tenancy roles and RLS (karry_owner / karry_app privileges) are completed in P1.5 (ADR-0004).
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username postgres --dbname karry \
  -v app_password="$KARRY_APP_PASSWORD" -v keycloak_password="$KEYCLOAK_DB_PASSWORD" <<'SQL'
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS timescaledb;
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- Runtime role: not a superuser, no BYPASSRLS, owns nothing.
CREATE ROLE karry_app LOGIN PASSWORD :'app_password' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
GRANT CONNECT ON DATABASE karry TO karry_app;

CREATE ROLE keycloak LOGIN PASSWORD :'keycloak_password' NOSUPERUSER NOCREATEDB NOCREATEROLE;
CREATE DATABASE keycloak OWNER keycloak;
SQL
