-- Spike schema for WP-P0-12. Executed by the table OWNER role (karry_spike_owner),
-- so that FORCE ROW LEVEL SECURITY is meaningful. The application connects as
-- karry_spike_app: not the owner, no BYPASSRLS, SELECT/INSERT only.
CREATE SCHEMA spike;

CREATE TABLE spike.shift_entry (
    id          uuid          PRIMARY KEY,
    tenant_id   uuid          NOT NULL,
    meter_value numeric(12,1) NOT NULL,
    created_at  timestamptz   NOT NULL DEFAULT now()
);
CREATE INDEX shift_entry_tenant_idx ON spike.shift_entry (tenant_id);

ALTER TABLE spike.shift_entry ENABLE ROW LEVEL SECURITY;
ALTER TABLE spike.shift_entry FORCE ROW LEVEL SECURITY;

-- nullif(...,'') matters: once a transaction-local setting has been used on a
-- session, later reads of current_setting(...,true) return '' (not NULL), and
-- ''::uuid would raise an error instead of failing closed with zero rows.
CREATE POLICY tenant_isolation ON spike.shift_entry
    USING      (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);

-- Control table: RLS enabled but NOT forced, to demonstrate that the owner bypasses it.
CREATE TABLE spike.unforced_entry (
    id        uuid PRIMARY KEY,
    tenant_id uuid NOT NULL
);
ALTER TABLE spike.unforced_entry ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON spike.unforced_entry
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);

-- Benchmark baseline table: same shape, no RLS (explicit WHERE instead).
CREATE TABLE spike.shift_entry_no_rls (LIKE spike.shift_entry INCLUDING ALL);

GRANT USAGE ON SCHEMA spike TO karry_spike_app;
GRANT SELECT, INSERT ON spike.shift_entry, spike.unforced_entry, spike.shift_entry_no_rls TO karry_spike_app;
