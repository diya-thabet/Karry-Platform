-- Karry database roles and privileges (ADR-0004, US-P1-TEN-02 AC3). Single source of truth for the local stack
-- (deploy/docker/postgres/init), the integration tests and the environments (P1.17).
--
-- Run as an administrator, connected to the Karry database. Idempotent. The caller sets the passwords first:
--   SET karry.owner_password = '…';  SET karry.app_password = '…';
--
--   karry_owner  owns every schema and table; used ONLY by the migration runner (`Karry.Host migrate`).
--   karry_app    runtime role of the API and workers: not an owner, no BYPASSRLS, data access only (DML), so
--                row-level security always applies to it and it cannot change the schema.
DO $roles$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'karry_owner') THEN
    EXECUTE format('CREATE ROLE karry_owner LOGIN PASSWORD %L', current_setting('karry.owner_password'));
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'karry_app') THEN
    EXECUTE format('CREATE ROLE karry_app LOGIN PASSWORD %L', current_setting('karry.app_password'));
  END IF;
  EXECUTE format('GRANT CONNECT, CREATE ON DATABASE %I TO karry_owner', current_database());
  EXECUTE format('GRANT CONNECT ON DATABASE %I TO karry_app', current_database());
END
$roles$;

-- Enforced on every run, also when the roles already existed with other attributes.
ALTER ROLE karry_owner NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
ALTER ROLE karry_app NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;

-- Nobody but the owner creates objects; the app gets data access to whatever the owner creates.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE karry_owner GRANT USAGE ON SCHEMAS TO karry_app;
ALTER DEFAULT PRIVILEGES FOR ROLE karry_owner GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO karry_app;
ALTER DEFAULT PRIVILEGES FOR ROLE karry_owner GRANT USAGE, SELECT ON SEQUENCES TO karry_app;
