-- inventory-service — 0001 clinic stock: catalogue, batches, and the append-only movement ledger.
-- Phase 25.5 · design 42 §5 · ADR-0029 (D1, D2, D5).
--
-- ============================================================================================================
-- THE TWO RULES THIS SCHEMA EXISTS TO ENFORCE
-- ============================================================================================================
--
-- 1. ON-HAND IS DERIVED, NEVER STORED. There is no `quantity_on_hand` column anywhere in this file, and that
--    is deliberate: on-hand = SUM(quantity) over `stock_movement`. A balance you can recompute is a balance
--    you can reconcile, and a balance you cannot reconcile is a number people stop trusting. It is the same
--    discipline as the audit chain and the approvals decision ledger, for the same reason. A physical
--    stock-take becomes a `Count` movement recording the VARIANCE, not an overwrite of history.
--
-- 2. CLINIC INVENTORY NEVER TOUCHES A PATIENT. There is no beneficiary_id, patient_id, encounter_id or
--    prescription_id column in ANY table here, and there never may be. Anything requiring a prescription goes
--    through pharmacy-service, against an Rx, with the authorization and benefit rules that entails. If clinic
--    inventory could issue medication to a beneficiary it would be a route around eligibility, coverage
--    limits, formulary and the dispense audit trail — every control the platform exists to enforce. Keeping
--    it PHI-free is also what lets a storekeeper use it without holding a clinical role.
--    `NoPhiInInventoryTests` asserts this over both the schema and the route table.
--
-- Conventions: uuid PKs, snake_case, audit columns, soft delete, *_history twins, tenant RLS.

CREATE SCHEMA IF NOT EXISTS inventory;

-- ============================================================================================================
-- item — the catalogue. Two categories with genuinely different rules (design 42 §5).
-- ============================================================================================================

CREATE TABLE IF NOT EXISTS inventory.item (
    item_id           uuid PRIMARY KEY,
    tenant_id         text NOT NULL CHECK (btrim(tenant_id) <> ''),
    sku               varchar(32)  NOT NULL,
    name_en           text NOT NULL,
    name_ar           text NOT NULL,

    category          varchar(12) NOT NULL CHECK (category IN ('Medical','NonMedical')),
    unit_of_measure   varchar(16) NOT NULL,

    is_batch_tracked  boolean NOT NULL DEFAULT false,
    requires_expiry   boolean NOT NULL DEFAULT false,

    -- D1 — CONTROLLED SUBSTANCES ARE EXCLUDED FROM V1 BY CONSTRAINT, NOT BY CONVENTION.
    --
    -- A controlled register needs dual signature, a running balance per ampoule and regulator-facing
    -- reporting: a module of its own, not a category flag. The column exists so the intent is legible and the
    -- CHECK pins it to false, which means enabling controlled substances is a deliberate, reviewable MIGRATION
    -- rather than someone ticking a checkbox in an admin screen at 4pm.
    is_controlled     boolean NOT NULL DEFAULT false,
    CONSTRAINT ck_item_no_controlled_substances CHECK (is_controlled = false),

    storage_condition varchar(32),
    cold_chain        boolean NOT NULL DEFAULT false,

    status            varchar(12) NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Discontinued')),

    -- MEDICAL ⇒ BATCH-TRACKED AND EXPIRY-TRACKED. Not a default a user can turn off: a medical consumable
    -- whose batch nobody recorded cannot be recalled, and one whose expiry nobody recorded cannot be blocked
    -- from issue. Both of those are the whole point of tracking medical stock separately.
    CONSTRAINT ck_item_medical_is_tracked CHECK (
        category <> 'Medical' OR (is_batch_tracked AND requires_expiry)),

    is_deleted        boolean NOT NULL DEFAULT false,
    row_version       integer NOT NULL DEFAULT 0,
    created_at        timestamptz NOT NULL DEFAULT now(),
    created_by        text,
    updated_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        text
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_item_sku ON inventory.item (tenant_id, sku) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS ix_item_category ON inventory.item (tenant_id, category) WHERE is_deleted = false;

-- ============================================================================================================
-- branch_item — per-clinic stocking policy. Reorder level and lead time differ by clinic: Aswan is four days
-- from a supplier that reaches Maadi overnight, and a single network-wide reorder level would either overstock
-- one or leave the other short.
-- ============================================================================================================

CREATE TABLE IF NOT EXISTS inventory.branch_item (
    branch_id      uuid NOT NULL,
    item_id        uuid NOT NULL REFERENCES inventory.item (item_id),
    tenant_id      text NOT NULL CHECK (btrim(tenant_id) <> ''),
    reorder_level  numeric(14,3) NOT NULL DEFAULT 0 CHECK (reorder_level >= 0),
    lead_time_days integer NOT NULL DEFAULT 0 CHECK (lead_time_days >= 0),
    is_stocked     boolean NOT NULL DEFAULT true,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    updated_by     text,
    PRIMARY KEY (branch_id, item_id)
);

-- ============================================================================================================
-- stock_batch — lot + expiry, for the items that require them.
-- ============================================================================================================

CREATE TABLE IF NOT EXISTS inventory.stock_batch (
    batch_id    uuid PRIMARY KEY,
    tenant_id   text NOT NULL CHECK (btrim(tenant_id) <> ''),
    item_id     uuid NOT NULL REFERENCES inventory.item (item_id),
    batch_no    varchar(64) NOT NULL,
    -- NULL only for a non-medical item; the trigger below enforces it against the item's category, which a
    -- CHECK cannot see from here.
    expiry_date date,
    created_at  timestamptz NOT NULL DEFAULT now(),
    created_by  text
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_stock_batch_item_no ON inventory.stock_batch (item_id, batch_no);
CREATE INDEX IF NOT EXISTS ix_stock_batch_expiry ON inventory.stock_batch (tenant_id, expiry_date)
    WHERE expiry_date IS NOT NULL;

-- A medical batch MUST carry an expiry. Enforced by trigger because the rule spans two tables, and enforced
-- at the DATABASE because "the endpoint validates it" is not a rule a data load respects.
CREATE OR REPLACE FUNCTION inventory.require_expiry_for_medical_batches()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE needs_expiry boolean;
BEGIN
    SELECT requires_expiry INTO needs_expiry FROM inventory.item WHERE item_id = NEW.item_id;
    IF needs_expiry AND NEW.expiry_date IS NULL THEN
        RAISE EXCEPTION 'inventory.stock_batch: item % requires an expiry date on every batch', NEW.item_id;
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_stock_batch_requires_expiry ON inventory.stock_batch;
CREATE TRIGGER trg_stock_batch_requires_expiry BEFORE INSERT OR UPDATE ON inventory.stock_batch
    FOR EACH ROW EXECUTE FUNCTION inventory.require_expiry_for_medical_batches();

-- ============================================================================================================
-- stock_movement — THE LEDGER. Append-only. The heart of the whole design.
-- ============================================================================================================

CREATE TABLE IF NOT EXISTS inventory.stock_movement (
    movement_id           uuid PRIMARY KEY,
    tenant_id             text NOT NULL CHECK (btrim(tenant_id) <> ''),
    branch_id             uuid NOT NULL,
    item_id               uuid NOT NULL REFERENCES inventory.item (item_id),
    batch_id              uuid REFERENCES inventory.stock_batch (batch_id),

    kind                  varchar(12) NOT NULL CHECK (kind IN
                          ('Receipt','Issue','TransferOut','TransferIn','Adjustment','WriteOff','Return','Count')),

    -- SIGNED BY KIND, and the sign is part of the record rather than derived at read time: on-hand is a plain
    -- SUM, so a reader never has to know the sign convention to get the right answer. Receipt/TransferIn/
    -- Return are positive; Issue/TransferOut/WriteOff are negative; Adjustment and Count may be either,
    -- because both record a VARIANCE that can go in both directions.
    quantity              numeric(14,3) NOT NULL CHECK (quantity <> 0),
    CONSTRAINT ck_stock_movement_sign CHECK (
        CASE kind
            WHEN 'Receipt'     THEN quantity > 0
            WHEN 'TransferIn'  THEN quantity > 0
            WHEN 'Return'      THEN quantity > 0
            WHEN 'Issue'       THEN quantity < 0
            WHEN 'TransferOut' THEN quantity < 0
            WHEN 'WriteOff'    THEN quantity < 0
            ELSE true            -- Adjustment / Count: a variance, either direction
        END),

    -- MANDATORY where the movement is not self-explaining. A receipt has a delivery note behind it; an
    -- adjustment, a write-off and a stock-take variance are somebody saying the records were wrong, and
    -- "no reason recorded" is what makes a ledger stop being evidence.
    reason                varchar(300),
    CONSTRAINT ck_stock_movement_reason CHECK (
        kind NOT IN ('Adjustment','WriteOff','Count') OR (reason IS NOT NULL AND btrim(reason) <> '')),

    -- Transfers are TWO PAIRED MOVEMENTS sharing one ref: TransferOut at the source, TransferIn at the
    -- destination. Nothing is created or destroyed in transit, and the pair sums to zero — asserted by test.
    transfer_ref          uuid,
    counterparty_branch_id uuid,
    CONSTRAINT ck_stock_movement_transfer CHECK (
        (kind NOT IN ('TransferOut','TransferIn') AND transfer_ref IS NULL AND counterparty_branch_id IS NULL)
        OR (kind IN ('TransferOut','TransferIn') AND transfer_ref IS NOT NULL AND counterparty_branch_id IS NOT NULL)),

    -- A transfer to your own branch is a no-op that would still write two rows and confuse every reconciliation.
    CONSTRAINT ck_stock_movement_counterparty CHECK (counterparty_branch_id IS NULL OR counterparty_branch_id <> branch_id),

    actor                 text NOT NULL,
    occurred_at           timestamptz NOT NULL DEFAULT now(),

    -- Idempotency: a double-posted receipt is a phantom stock level, and the ledger has no UPDATE to correct
    -- it with. Stable per INTENT, never per attempt.
    idempotency_key       text NOT NULL,

    created_at            timestamptz NOT NULL DEFAULT now()

    -- NOTE THE ABSENCES, and they are the design:
    --   * no quantity_on_hand, here or anywhere — on-hand is SUM(quantity) (rule 1 in the header)
    --   * no beneficiary_id / patient_id / encounter_id / prescription_id (rule 2, and D2)
    --   * no is_deleted / updated_at — this table is APPEND-ONLY; a mistake is corrected by a further
    --     movement, which is what makes the history reconstructable
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_stock_movement_idempotency
    ON inventory.stock_movement (tenant_id, idempotency_key);
CREATE INDEX IF NOT EXISTS ix_stock_movement_balance
    ON inventory.stock_movement (tenant_id, branch_id, item_id, batch_id);
CREATE INDEX IF NOT EXISTS ix_stock_movement_ledger
    ON inventory.stock_movement (tenant_id, branch_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_stock_movement_transfer_ref
    ON inventory.stock_movement (transfer_ref) WHERE transfer_ref IS NOT NULL;

-- APPEND-ONLY, enforced twice: a trigger that refuses UPDATE/DELETE even for a mis-granted role, and the
-- withheld grant below. Same belt-and-braces as approvals.authorization_decision, and for the same reason —
-- a ledger that can be edited is a balance nobody can reconcile.
CREATE OR REPLACE FUNCTION inventory.deny_movement_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'inventory.stock_movement is append-only: % is denied. Correct a mistake with a further movement.', TG_OP;
END $$;

DROP TRIGGER IF EXISTS trg_stock_movement_no_mutate ON inventory.stock_movement;
CREATE TRIGGER trg_stock_movement_no_mutate BEFORE UPDATE OR DELETE ON inventory.stock_movement
    FOR EACH ROW EXECUTE FUNCTION inventory.deny_movement_mutation();

-- ============================================================================================================
-- on-hand — the DERIVED balance, exposed as a view so callers cannot accidentally invent a second definition.
-- ============================================================================================================

CREATE OR REPLACE VIEW inventory.stock_on_hand AS
SELECT m.tenant_id,
       m.branch_id,
       m.item_id,
       m.batch_id,
       SUM(m.quantity) AS on_hand
FROM inventory.stock_movement m
GROUP BY m.tenant_id, m.branch_id, m.item_id, m.batch_id;

-- ============================================================================================================
-- history twins (append-only) for the two MUTABLE tables. stock_movement needs none: it is already the
-- history.
-- ============================================================================================================

CREATE TABLE IF NOT EXISTS inventory.item_history (
    history_id   bigserial PRIMARY KEY,
    item_id      uuid NOT NULL,
    tenant_id    text NOT NULL CHECK (btrim(tenant_id) <> ''),
    operation    text NOT NULL,
    row_snapshot jsonb NOT NULL,
    recorded_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_item_history_id ON inventory.item_history (item_id);

CREATE OR REPLACE FUNCTION inventory.write_item_history()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO inventory.item_history (item_id, tenant_id, operation, row_snapshot)
    VALUES (NEW.item_id, NEW.tenant_id, TG_OP, to_jsonb(NEW));
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_item_history ON inventory.item;
CREATE TRIGGER trg_item_history AFTER INSERT OR UPDATE ON inventory.item
    FOR EACH ROW EXECUTE FUNCTION inventory.write_item_history();

CREATE TABLE IF NOT EXISTS inventory.branch_item_history (
    history_id   bigserial PRIMARY KEY,
    branch_id    uuid NOT NULL,
    item_id      uuid NOT NULL,
    tenant_id    text NOT NULL CHECK (btrim(tenant_id) <> ''),
    operation    text NOT NULL,
    row_snapshot jsonb NOT NULL,
    recorded_at  timestamptz NOT NULL DEFAULT now()
);

CREATE OR REPLACE FUNCTION inventory.write_branch_item_history()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO inventory.branch_item_history (branch_id, item_id, tenant_id, operation, row_snapshot)
    VALUES (NEW.branch_id, NEW.item_id, NEW.tenant_id, TG_OP, to_jsonb(NEW));
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_branch_item_history ON inventory.branch_item;
CREATE TRIGGER trg_branch_item_history AFTER INSERT OR UPDATE ON inventory.branch_item
    FOR EACH ROW EXECUTE FUNCTION inventory.write_branch_item_history();

-- ============================================================================================================
-- grants + tenant RLS
-- ============================================================================================================

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

GRANT USAGE ON SCHEMA inventory TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA inventory TO hbmp_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA inventory TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA inventory GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO hbmp_app;

-- The ledger: INSERT and SELECT only. The trigger above already refuses, but a withheld grant means the
-- attempt never reaches it — two independent controls, because this one matters.
REVOKE UPDATE, DELETE ON inventory.stock_movement FROM hbmp_app;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['item','branch_item','stock_batch','stock_movement',
                             'item_history','branch_item_history']
    LOOP
        EXECUTE format('ALTER TABLE inventory.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE inventory.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON inventory.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON inventory.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;
