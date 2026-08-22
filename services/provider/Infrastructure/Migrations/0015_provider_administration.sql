-- provider-service — 0015: the columns a provider record needs to be ADMINISTERED, and the history
-- twins for the two tables that carry the money and the map. ADDITIVE (expand phase).
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- `provider.provider` has held six meaningful columns since 0001 — a code, a legal name, a type, a status, an
-- onboarding state and two timestamps. Everything the Network Team actually administers about a counterparty
-- lived somewhere else: the trading name on the signage, the tax card number the finance team reconciles
-- against, a phone number for the person who answers when a referral goes wrong, and — most of all — WHY a
-- provider was suspended and WHO decided it.
--
-- That last one is not a nicety. Suspending a provider revokes every one of their users and drops them out of
-- the routable network; the audit chain records that it happened, behind `audit:read`, which is Security and
-- Compliance. The person running the network — who has to decide next month whether to switch them back on —
-- had no way to read the reason for their own decision.
--
-- ============================================================================================================
-- provider_history HAS HAD NO ROW-LEVEL SECURITY SINCE 0001
-- ============================================================================================================
-- 0001 created `provider.provider_history` with a trigger that snapshots every insert and update. 0003 then
-- put RLS on `provider`, `provider_location`, `provider_contract`, `provider_credential`, `provider_user` and
-- `contract_service_line` — and not on the history table, which has neither a tenant_id column to filter on
-- nor a policy that could use one. Every provider row this platform has ever written, for every tenant, sits
-- in one unfiltered table.
--
-- Nothing has leaked, for the only reason that matters here: nothing has ever read it. There is no endpoint,
-- no query, no report. The table has been write-only for its entire life — which is also why the gap survived
-- three security passes. 19.9 adds the read, so the gap has to close in the same migration that opens it.
--
-- tenant_id is backfilled from the snapshot itself: `row_snapshot` is `to_jsonb(NEW)` of a table whose
-- tenant_id has been NOT NULL since 0001, so every historical row can name its own tenant. The NOT NULL and
-- the fail-closed policy then follow 0014's shape exactly.
--
-- ============================================================================================================
-- WHY LOCATIONS AND CONTRACTS GET THEIR OWN HISTORY
-- ============================================================================================================
-- A provider-level snapshot cannot record that somebody moved the primary location to a different governorate
-- or shortened a contract's effective window, because neither of those touches the provider row. Those are
-- the two edits with consequences a month later — routing sends patients to an address, and claims price
-- against a contract's dates — so they get the same twin, on the same trigger shape, read at the same
-- operational authority as the row itself.

-- ============================================================================================================
-- 1. provider — identity beyond the legal name, and the actor behind every change
-- ============================================================================================================

ALTER TABLE provider.provider
    -- The name on the building, when it differs from the name on the contract. Referrals and the directory
    -- show this one; the legal name is what the contract and the tax card carry.
    ADD COLUMN IF NOT EXISTS commercial_name  varchar(160),
    ADD COLUMN IF NOT EXISTS tax_id           varchar(32),
    ADD COLUMN IF NOT EXISTS phone            varchar(32),
    ADD COLUMN IF NOT EXISTS email            varchar(160),
    ADD COLUMN IF NOT EXISTS notes            text,
    -- The reason for the CURRENT standing, and who set it. Distinct from the audit chain: this is the
    -- operational record, readable by the team that administers the provider.
    ADD COLUMN IF NOT EXISTS status_reason    text,
    ADD COLUMN IF NOT EXISTS status_actor     text,
    ADD COLUMN IF NOT EXISTS status_actor_name text,
    ADD COLUMN IF NOT EXISTS status_changed_at timestamptz,
    ADD COLUMN IF NOT EXISTS created_by       text,
    ADD COLUMN IF NOT EXISTS created_by_name  text,
    ADD COLUMN IF NOT EXISTS updated_by       text,
    ADD COLUMN IF NOT EXISTS updated_by_name  text;

-- An email that is stored has to be usable: a blank string is not an absent email, and a directory that
-- renders one teaches an operator there is somebody to write to.
ALTER TABLE provider.provider
    ADD CONSTRAINT ck_provider_email_shape CHECK (email IS NULL OR email LIKE '%_@_%') NOT VALID;

-- ============================================================================================================
-- 2. provider_history — the tenant column, the policy, and the grants it never had
-- ============================================================================================================

ALTER TABLE provider.provider_history
    ADD COLUMN IF NOT EXISTS tenant_id text;

UPDATE provider.provider_history
   SET tenant_id = row_snapshot ->> 'tenant_id'
 WHERE tenant_id IS NULL;

-- Safe despite the rule this acknowledges: the ONLY writer to this table is the trigger below, which lives
-- in the database and is replaced in this same migration. There is no deployed application version that
-- inserts here, so no running writer can be caught out by the constraint.
ALTER TABLE provider.provider_history
    ALTER COLUMN tenant_id SET NOT NULL;  -- migrate-compat: contract-ok (sole writer is the trigger replaced below)

ALTER TABLE provider.provider_history
    ADD CONSTRAINT ck_provider_history_tenant_not_blank CHECK (btrim(tenant_id) <> '');

CREATE OR REPLACE FUNCTION provider.write_provider_history()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO provider.provider_history (provider_id, tenant_id, operation, row_snapshot)
    VALUES (NEW.provider_id, NEW.tenant_id, TG_OP, to_jsonb(NEW));
    RETURN NEW;
END $$;

CREATE INDEX IF NOT EXISTS ix_provider_history_id
    ON provider.provider_history (provider_id, history_id);

-- ============================================================================================================
-- 3. provider_location — deactivation with a reason, and its own history
-- ============================================================================================================

ALTER TABLE provider.provider_location
    ADD COLUMN IF NOT EXISTS deactivated_at     timestamptz,
    ADD COLUMN IF NOT EXISTS deactivated_by     text,
    ADD COLUMN IF NOT EXISTS deactivation_reason text,
    ADD COLUMN IF NOT EXISTS created_by         text,
    ADD COLUMN IF NOT EXISTS updated_by         text,
    ADD COLUMN IF NOT EXISTS updated_by_name    text,
    ADD COLUMN IF NOT EXISTS updated_at         timestamptz;

CREATE TABLE IF NOT EXISTS provider.provider_location_history (
    history_id   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    location_id  uuid NOT NULL,
    provider_id  uuid NOT NULL,
    tenant_id    text NOT NULL,
    operation    text NOT NULL,
    row_snapshot jsonb NOT NULL,
    recorded_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_location_history_id
    ON provider.provider_location_history (location_id, history_id);

CREATE OR REPLACE FUNCTION provider.write_location_history()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO provider.provider_location_history (location_id, provider_id, tenant_id, operation, row_snapshot)
    VALUES (NEW.location_id, NEW.provider_id, NEW.tenant_id, TG_OP, to_jsonb(NEW));
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_location_history ON provider.provider_location;
CREATE TRIGGER trg_location_history AFTER INSERT OR UPDATE ON provider.provider_location
    FOR EACH ROW EXECUTE FUNCTION provider.write_location_history();

-- ============================================================================================================
-- 4. provider_contract — why it ended, and its own history
-- ============================================================================================================

ALTER TABLE provider.provider_contract
    ADD COLUMN IF NOT EXISTS status_reason     text,
    ADD COLUMN IF NOT EXISTS status_actor      text,
    ADD COLUMN IF NOT EXISTS status_actor_name text,
    ADD COLUMN IF NOT EXISTS status_changed_at timestamptz,
    ADD COLUMN IF NOT EXISTS created_by        text,
    ADD COLUMN IF NOT EXISTS created_by_name   text,
    ADD COLUMN IF NOT EXISTS updated_by        text,
    ADD COLUMN IF NOT EXISTS updated_by_name   text,
    ADD COLUMN IF NOT EXISTS updated_at        timestamptz;

CREATE TABLE IF NOT EXISTS provider.provider_contract_history (
    history_id   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    contract_id  uuid NOT NULL,
    provider_id  uuid NOT NULL,
    tenant_id    text NOT NULL,
    operation    text NOT NULL,
    row_snapshot jsonb NOT NULL,
    recorded_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_contract_history_id
    ON provider.provider_contract_history (contract_id, history_id);

CREATE OR REPLACE FUNCTION provider.write_contract_history()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO provider.provider_contract_history (contract_id, provider_id, tenant_id, operation, row_snapshot)
    VALUES (NEW.contract_id, NEW.provider_id, NEW.tenant_id, TG_OP, to_jsonb(NEW));
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_contract_history ON provider.provider_contract;
CREATE TRIGGER trg_contract_history AFTER INSERT OR UPDATE ON provider.provider_contract
    FOR EACH ROW EXECUTE FUNCTION provider.write_contract_history();

-- ============================================================================================================
-- 5. RLS + grants on all three history twins (0014's shape, fail-closed on an unset tenant GUC)
-- ============================================================================================================

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['provider_history','provider_location_history','provider_contract_history']
    LOOP
        EXECUTE format('GRANT SELECT, INSERT ON provider.%I TO hbmp_app', t);
        EXECUTE format('ALTER TABLE provider.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE provider.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON provider.%1$s', t);
        -- Fail-CLOSED: an unset or empty app.tenant_id matches nothing at all.
        EXECUTE format(
            'CREATE POLICY rls_%1$s ON provider.%1$s USING (tenant_id = current_setting(''app.tenant_id'', true))', t);
    END LOOP;
END $$;

ALTER TABLE provider.provider_location_history
    ADD CONSTRAINT ck_location_history_tenant_not_blank CHECK (btrim(tenant_id) <> '');
ALTER TABLE provider.provider_contract_history
    ADD CONSTRAINT ck_contract_history_tenant_not_blank CHECK (btrim(tenant_id) <> '');
