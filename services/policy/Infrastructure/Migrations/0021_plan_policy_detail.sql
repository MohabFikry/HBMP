-- policy-service — 0021: the plan and the policy become records somebody can administer, and each gets a
-- history twin. ADDITIVE (expand phase). Every column is nullable; nothing existing changes shape.
--
-- ============================================================================================================
-- THE SAME GAP, TWO LEVELS DOWN
-- ============================================================================================================
-- 0020 gave the PAYER the facts and the history it needed to be administered rather than merely labelled.
-- The two entities beneath it had the identical gap and it had the identical cause: a create endpoint, a list,
-- and nothing else.
--
--   · `policy.plan`   — created, then never correctable. A typo in a plan's Arabic name was permanent, and
--                       `status` accepted 'Inactive' with no code path able to write it. A plan withdrawn
--                       from sale stayed indistinguishable from one still being enrolled onto.
--   · `policy.policy` — created and renewed, never edited. `status` accepts 'Suspended' and nothing has ever
--                       set it, so the state a contract enters when a payer stops paying could not be
--                       recorded at all; `max_members` and the effective window were fixed at creation; and
--                       neither row could say WHO last touched it, because neither has ever had a
--                       created_by/updated_by column.
--
-- ============================================================================================================
-- WHY THE POLICY'S SIGNATURE COLUMNS ARRIVE ONLY NOW
-- ============================================================================================================
-- `policy.policy` is the oldest table in this schema (0001). It has carried `created_at` and `updated_at`
-- since the first migration and has never recorded a subject. That was survivable while the only write was
-- the create — the audit event named the actor and the row was never touched again. It stops being
-- survivable the moment the row becomes editable, because "who suspended this contract, and when" is the
-- first question anybody asks of a suspended contract.
--
-- ============================================================================================================
-- SUSPENDING IS NOT DEACTIVATING
-- ============================================================================================================
-- Worth stating here because the endpoints enforce it. Deactivating a PAYER is refused while it still funds
-- live policies (0020) — it is a catalogue action, and cascading it would end cover nobody reviewed.
-- Suspending a POLICY is the opposite: it is the operational action itself, the thing that happens when a
-- payer stops paying, and it necessarily affects live members. Refusing it would be refusing the operation.
-- So the reason is mandatory, the member count is REPORTED back so the confirmation can state the impact,
-- and the write proceeds. Same shape of control, opposite answer, because the domains differ.

-- ---- plan --------------------------------------------------------------------------------------------

ALTER TABLE policy.plan
    ADD COLUMN IF NOT EXISTS status_reason     text,
    ADD COLUMN IF NOT EXISTS status_changed_at timestamptz,
    ADD COLUMN IF NOT EXISTS status_changed_by uuid,
    ADD COLUMN IF NOT EXISTS created_by_name   text,
    ADD COLUMN IF NOT EXISTS updated_by_name   text;

CREATE TABLE IF NOT EXISTS policy.plan_history (
    history_id   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    plan_id      uuid NOT NULL,
    tenant_id    text NOT NULL,
    operation    text NOT NULL,
    row_snapshot jsonb NOT NULL,
    recorded_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_plan_history_id ON policy.plan_history (plan_id, history_id);

CREATE OR REPLACE FUNCTION policy.write_plan_history()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO policy.plan_history (plan_id, tenant_id, operation, row_snapshot)
    VALUES (NEW.plan_id, NEW.tenant_id, TG_OP, to_jsonb(NEW));
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_plan_history ON policy.plan;
CREATE TRIGGER trg_plan_history AFTER INSERT OR UPDATE ON policy.plan
    FOR EACH ROW EXECUTE FUNCTION policy.write_plan_history();

-- ---- policy ------------------------------------------------------------------------------------------

ALTER TABLE policy.policy
    ADD COLUMN IF NOT EXISTS created_by        uuid,
    ADD COLUMN IF NOT EXISTS created_by_name   text,
    ADD COLUMN IF NOT EXISTS updated_by        uuid,
    ADD COLUMN IF NOT EXISTS updated_by_name   text,
    ADD COLUMN IF NOT EXISTS status_reason     text,
    ADD COLUMN IF NOT EXISTS status_changed_at timestamptz,
    ADD COLUMN IF NOT EXISTS status_changed_by uuid,
    -- Administrative notes on the contract. NOT clinical, and nothing about a member: this is the row that
    -- carries commercial terms, and its notes carry commercial context.
    ADD COLUMN IF NOT EXISTS notes             text;

CREATE TABLE IF NOT EXISTS policy.policy_history (
    history_id   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    policy_id    uuid NOT NULL,
    tenant_id    text NOT NULL,
    operation    text NOT NULL,
    row_snapshot jsonb NOT NULL,
    recorded_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_policy_history_id ON policy.policy_history (policy_id, history_id);

CREATE OR REPLACE FUNCTION policy.write_policy_history()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO policy.policy_history (policy_id, tenant_id, operation, row_snapshot)
    VALUES (NEW.policy_id, NEW.tenant_id, TG_OP, to_jsonb(NEW));
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_policy_history ON policy.policy;
CREATE TRIGGER trg_policy_history AFTER INSERT OR UPDATE ON policy.policy
    FOR EACH ROW EXECUTE FUNCTION policy.write_policy_history();

-- ---- grants + tenant RLS (ADR-0011, the shape 0002/0005/0020 use) -------------------------------------

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

GRANT SELECT, INSERT ON policy.plan_history, policy.policy_history TO hbmp_app;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['plan_history','policy_history']
    LOOP
        EXECUTE format('ALTER TABLE policy.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE policy.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON policy.%1$s', t);
        -- Fail-CLOSED: an unset or empty app.tenant_id matches nothing.
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON policy.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;

-- A tenant_id of '' belongs to no tenant. New tables start with the constraint rather than acquiring it in a
-- later backfill (0016_no_unscoped_rows's lesson).
ALTER TABLE policy.plan_history
    DROP CONSTRAINT IF EXISTS ck_plan_history_tenant_not_blank;  -- migrate-compat: contract-ok (idempotency guard on a table created in THIS migration — there is no deployed reader to break)
ALTER TABLE policy.plan_history
    ADD CONSTRAINT ck_plan_history_tenant_not_blank CHECK (btrim(tenant_id) <> '');
ALTER TABLE policy.policy_history
    DROP CONSTRAINT IF EXISTS ck_policy_history_tenant_not_blank;  -- migrate-compat: contract-ok (idempotency guard on a table created in THIS migration — there is no deployed reader to break)
ALTER TABLE policy.policy_history
    ADD CONSTRAINT ck_policy_history_tenant_not_blank CHECK (btrim(tenant_id) <> '');
