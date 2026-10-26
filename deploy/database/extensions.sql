-- Extensions of the karry database, created by an administrator before any migration (ADR-0024: migrations run as
-- karry_owner, which cannot create extensions; a migration that needs one declares it with IF NOT EXISTS, a no-op here).
-- Used by deploy/docker/postgres/init/10-karry.sh and by the test databases (tests/Karry.Testing/PostgresServer.cs).
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS timescaledb;
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
