# Spike WP-P0-13 — PostgreSQL major version & extensions

**Question.** Can we run PostgreSQL 17 or 18 with **PostGIS + TimescaleDB + pgvector + pgcrypto** together, locally/CI (Docker) and on the managed provider we will choose?

**Status:** ⏳ Partially automated. [`check-extensions.sh`](check-extensions.sh) runs in GitHub Actions against `timescale/timescaledb-ha:pg17` and `:pg18` and writes a version table to the job summary. It could not run on the developer workstation (no Docker permission, see hand-off log).

**Still open (owner + architect):** the managed-provider check depends on the hosting decision (WP-P0-14). Candidates to verify: Timescale Cloud, Azure Database for PostgreSQL Flexible Server, AWS RDS/Aurora, Scaleway/OVH managed PostgreSQL. Several managed services do **not** offer TimescaleDB; if the chosen provider lacks it, fall back to native partitioning (`pg_partman`) for telemetry and record that in ADR-0003.

**Decision rule:** pick the highest major version for which the CI check passes **and** the chosen provider supports all four extensions.
