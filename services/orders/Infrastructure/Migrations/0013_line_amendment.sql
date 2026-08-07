-- orders-service — 0013 amend & cancel a SIGNED order, at LINE level (phase 30 Gate 1, design 46 §1/§3).
--
-- ============================================================================================================
-- YOU DO NOT EDIT A SIGNED CLINICAL RECORD — YOU SUPERSEDE IT
-- ============================================================================================================
-- A signed order is a legal clinical record and the basis of a fulfilment decision. Editing the row in place
-- destroys the answer to "what was actually ordered on the 4th?", which is the question asked when something
-- goes wrong. So amend = INSERT the new version + mark the original Superseded. The original is never mutated.
--
-- The freeze is a TRIGGER, not a convention. The API can answer a body edit with 409, but a repair script, a
-- future endpoint or a psql session walks straight past that — the same reasoning policy.note's append-only
-- trigger records (policy 0009), and the same reasoning that put the consume invariant in a CHECK.
--
-- ============================================================================================================
-- WHY LINE LEVEL AND NOT ORDER LEVEL
-- ============================================================================================================
-- Design 46 §3: the amendable scope is WHATEVER HAS NOT BEEN CONSUMED. A 3-line order with line 1's sample
-- already taken is amendable in lines 2 and 3 — line 1 is fact. An order-level model cannot express that, so
-- it would have to refuse the whole request or silently do half, and both are wrong.
--
-- A whole-order cancel is therefore "cancel every still-cancellable line", reporting partial success plainly.
--
-- ============================================================================================================
-- 'Superseded' GOES ON THE LINE CHECK ONLY — A DELIBERATE DEVIATION FROM THE PROMPT
-- ============================================================================================================
-- The phase-30 prompt says to add 'Superseded' to "the line/order tables ... status CHECK". It is added HERE
-- to order_line only, and NOT to investigation_order, because an order with one superseded line and two live
-- ones is not superseded — it is an order that has been partly amended. There is no transition that could
-- reach a head status of Superseded, and a status nothing can enter is a status somebody will eventually set
-- by hand, on the aggregate whose roll-up drives whether a technician sees the work at all.
--
-- The head's amendment history is its lines'. Recorded in ADR-0030 and in docs/phase-30-gate-0-audit.md.
--
-- Additive + idempotent.

-- ============================================================================================================
-- 1. The coded reason vocabulary
-- ============================================================================================================
-- CODED, not free text alone. The codes are what make "how often do we cancel, and why" answerable, and they
-- feed the medical director's quality reporting; free text alone answers nothing at scale. Free text is
-- ADDITIONAL — a code without the sentence loses the specifics of this case.
--
-- NO tenant_id, deliberately: a clinical-error taxonomy means the same thing for every tenant, exactly as
-- masterdata's catalogues do. That also keeps it out of the RLS house-pattern rule honestly rather than by
-- exemption.
--
-- The identical vocabulary is seeded in pharmacy 0013. Two copies rather than one shared table because the
-- FK must be real and the write path must not depend on another service being reachable — a doctor must be
-- able to cancel a prescription when masterdata is down. AmendmentReasonSeedTests fails the build if the two
-- copies ever drift.

CREATE TABLE IF NOT EXISTS orders.amendment_reason (
    code        varchar(32) PRIMARY KEY,
    name_en     text        NOT NULL,
    name_ar     text        NOT NULL,
    -- Which order kinds may cite it. 'All' or a comma-free single kind; the picker filters on it so a
    -- pharmacy-only reason never appears on a lab order.
    applies_to  varchar(16) NOT NULL DEFAULT 'All'
                CHECK (applies_to IN ('All','Prescription','Order')),
    is_active   boolean     NOT NULL DEFAULT true,
    sort_order  int         NOT NULL DEFAULT 0,
    created_at  timestamptz NOT NULL DEFAULT now()
);

INSERT INTO orders.amendment_reason (code, name_en, name_ar, applies_to, sort_order) VALUES
    ('PrescribingError', 'Prescribing error',  'خطأ في الوصف',        'All',          10),
    ('DoseCorrection',   'Dose correction',    'تصحيح الجرعة',        'Prescription', 20),
    ('PatientDeclined',  'Patient declined',   'رفض المريض',          'All',          30),
    ('ClinicalChange',   'Clinical change',    'تغير الحالة السريرية', 'All',          40),
    ('Duplicate',        'Duplicate',          'مكرر',                'All',          50),
    ('DrugUnavailable',  'Drug unavailable',   'الدواء غير متوفر',     'Prescription', 60),
    ('NotEligible',      'Patient not eligible','المريض غير مؤهل',     'All',          70),
    ('Other',            'Other',              'أخرى',                'All',         900)
ON CONFLICT (code) DO NOTHING;

-- ============================================================================================================
-- 2. The version chain on the line
-- ============================================================================================================

ALTER TABLE orders.order_line
    ADD COLUMN IF NOT EXISTS version_no             int  NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS supersedes_id          uuid NULL REFERENCES orders.order_line(order_line_id),
    ADD COLUMN IF NOT EXISTS superseded_by_id       uuid NULL REFERENCES orders.order_line(order_line_id),
    -- The FIRST version in the chain (itself, on v1). supersedes_id chains backwards one step at a time;
    -- root_line_id answers "every version of this line" in one indexed query, which is what the service-history
    -- modal, the fulfiller's queue detail and the order notes of Gate 5b all need. A recursive walk would work
    -- and would be re-derived, slightly differently, at each of those three call sites.
    ADD COLUMN IF NOT EXISTS root_line_id           uuid NULL,
    ADD COLUMN IF NOT EXISTS amendment_reason_code  varchar(32)  NULL REFERENCES orders.amendment_reason(code),
    ADD COLUMN IF NOT EXISTS amendment_reason_text  varchar(300) NULL,
    ADD COLUMN IF NOT EXISTS amended_by             uuid NULL,
    ADD COLUMN IF NOT EXISTS amended_at             timestamptz NULL;

-- Existing rows are v1 of their own chain.
UPDATE orders.order_line SET root_line_id = order_line_id WHERE root_line_id IS NULL;
-- NOT NULL is DEFERRED to deferred/0014, not applied here. During a rolling deploy an OLD replica
-- still inserts order_line rows without root_line_id, and a NOT NULL on this column would turn that into a
-- constraint violation — i.e. an order a doctor cannot place, mid-encounter. Expand now, contract
-- once the switch deploy has fully rolled out (the same discipline as the radiology rename).
-- The new code fills it at the DbContext choke point, so no new row is ever written without it.

CREATE INDEX IF NOT EXISTS ix_order_line_root ON orders.order_line (root_line_id, version_no);
CREATE INDEX IF NOT EXISTS ix_order_line_superseded_by ON orders.order_line (superseded_by_id)
    WHERE superseded_by_id IS NOT NULL;

-- 0001 created the status CHECK inline, so its name is server-generated. Drop every CHECK on this table that
-- mentions `status` and re-add all three below, which makes the block idempotent and does not depend on
-- guessing a constraint name: a DROP IF EXISTS that silently matched nothing would leave the old constraint
-- in place, the new status unusable, and the ADD failing on a duplicate.
DO $$  -- migrate-compat: contract-ok (WIDENS the status CHECK to admit 'Superseded'; the old, narrower constraint is replaced in the same migration, so no value that was legal before becomes illegal)
DECLARE c record;
BEGIN
    FOR c IN SELECT conname FROM pg_constraint
             WHERE conrelid = 'orders.order_line'::regclass AND contype = 'c'
               AND pg_get_constraintdef(oid) LIKE '%status%'
    LOOP
        EXECUTE format('ALTER TABLE orders.order_line DROP CONSTRAINT %I', c.conname);
    END LOOP;
END $$;

ALTER TABLE orders.order_line
    ADD CONSTRAINT ck_order_line_status CHECK (
        status IN ('Active','PartiallyUsed','Completed','Cancelled','Superseded'));

-- A line that left the live set says WHY, WHO and WHEN, or it did not leave it. A cancellation without those
-- three is a row that changed state for reasons nobody recorded, on the surface most likely to be read back
-- in a dispute — the same rule policy.note's ck_note_cancellation_complete states.
--
-- NOT VALID: rows cancelled before this migration carry no reason, and inventing one for them would be a
-- worse lie than leaving the gap. Postgres still enforces this on every INSERT and UPDATE from here on, which
-- is the whole population the rule is for; it only skips the retrospective scan.
ALTER TABLE orders.order_line
    ADD CONSTRAINT ck_order_line_amendment_attributed CHECK (
        status NOT IN ('Cancelled','Superseded')
        OR (amendment_reason_code IS NOT NULL AND amended_by IS NOT NULL AND amended_at IS NOT NULL)
    ) NOT VALID;

-- A superseded line points at its successor; a live one does not.
ALTER TABLE orders.order_line
    ADD CONSTRAINT ck_order_line_superseded_has_successor CHECK (
        (status = 'Superseded') = (superseded_by_id IS NOT NULL));

-- ============================================================================================================
-- 3. NOTHING SIGNED IS MUTATED — enforced by the database
-- ============================================================================================================
-- The consume path must keep updating quantity_consumed and status; that is the accumulator moving forward,
-- not the record changing. What is frozen is everything that says WHAT WAS ORDERED.
--
-- sensitivity_level is frozen too, which phase 14.6 already required in prose ("pinned at order creation so
-- later reclassification cannot retroactively unlock already-restricted data") and nothing enforced.

-- WHY NOT A DELETE BRANCH HERE. "Nothing is deleted, ever" (design 46 §1) is enforced one step earlier, by
-- REVOKING the privilege from hbmp_app below. Every service runs as that role, so the application cannot
-- attempt a delete at all — which is stronger than being refused one, and is the argument policy 0009 already
-- makes ("withholding the privilege means a bug cannot attempt it"). A trigger would additionally block the
-- SCHEMA OWNER, whose deletes are maintenance rather than application traffic; and a superuser who wanted to
-- delete could drop the trigger anyway, so it never protected against that reader in the first place.
CREATE OR REPLACE FUNCTION orders.guard_order_line_signed()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.order_id            IS DISTINCT FROM NEW.order_id
       OR OLD.code_system      IS DISTINCT FROM NEW.code_system
       OR OLD.code             IS DISTINCT FROM NEW.code
       OR OLD.description      IS DISTINCT FROM NEW.description
       OR OLD.quantity_ordered IS DISTINCT FROM NEW.quantity_ordered
       OR OLD.requested_quantity  IS DISTINCT FROM NEW.requested_quantity
       OR OLD.procedure_type_code IS DISTINCT FROM NEW.procedure_type_code
       OR OLD.examination_type_id IS DISTINCT FROM NEW.examination_type_id
       OR OLD.sensitivity_level   IS DISTINCT FROM NEW.sensitivity_level THEN
        RAISE EXCEPTION
            'order line % is signed clinical content and can never be edited in place — supersede it (design 46 §1)',
            OLD.order_line_id USING ERRCODE = 'raise_exception';
    END IF;

    IF OLD.version_no IS DISTINCT FROM NEW.version_no
       OR OLD.supersedes_id IS DISTINCT FROM NEW.supersedes_id
       OR OLD.root_line_id  IS DISTINCT FROM NEW.root_line_id THEN
        RAISE EXCEPTION 'the version chain of line % is immutable', OLD.order_line_id
            USING ERRCODE = 'raise_exception';
    END IF;

    -- A terminal line does not come back. Reinstating a cancelled line would let a withdrawn order be
    -- fulfilled with nothing in the record saying it had ever been withdrawn.
    IF OLD.status IN ('Cancelled','Superseded') AND NEW.status IS DISTINCT FROM OLD.status THEN
        RAISE EXCEPTION 'line % is %; it cannot be reinstated — raise a new order', OLD.order_line_id, OLD.status
            USING ERRCODE = 'raise_exception';
    END IF;

    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_order_line_signed ON orders.order_line;
CREATE TRIGGER trg_order_line_signed BEFORE UPDATE ON orders.order_line
    FOR EACH ROW EXECUTE FUNCTION orders.guard_order_line_signed();

-- NOTHING IS DELETED, EVER (design 46 §1, invariant 1). 0006 granted DELETE on every table in the schema;
-- these two are the clinical record, so the grant is withdrawn from the runtime role here.
REVOKE DELETE ON orders.order_line FROM hbmp_app;
REVOKE DELETE ON orders.investigation_order FROM hbmp_app;

-- ============================================================================================================
-- 4. The amendment ledger
-- ============================================================================================================
-- Append-only, one row per applied amendment or cancellation, keyed by a UNIQUE idempotency key — the SAME
-- duplicate-proof anchor order_fulfillment and dispense_event use. That is what makes a double-tapped cancel
-- apply once rather than writing two amendment records (Gate 2), and it is deliberately not a second
-- mechanism: the one in ConsumeExecutor took several phases to get right.

CREATE TABLE IF NOT EXISTS orders.line_amendment (
    amendment_id      uuid PRIMARY KEY,
    tenant_id         text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',
    order_id          uuid NOT NULL REFERENCES orders.investigation_order(order_id),
    -- The line as it was when the action was taken. For an Amend this is the row that became Superseded.
    order_line_id     uuid NOT NULL REFERENCES orders.order_line(order_line_id),
    -- The row created by an Amend. NULL for a Cancel: cancelling creates no successor.
    new_line_id       uuid NULL REFERENCES orders.order_line(order_line_id),

    action            varchar(10) NOT NULL CHECK (action IN ('Cancel','Amend')),
    from_status       varchar(20) NOT NULL,
    to_status         varchar(20) NOT NULL,

    reason_code       varchar(32)  NOT NULL REFERENCES orders.amendment_reason(code),
    reason_text       varchar(300) NULL,

    amended_by        uuid NOT NULL,
    amended_by_display varchar(200) NULL,
    amended_at        timestamptz NOT NULL DEFAULT now(),

    -- UNIQUE: the duplicate-proof anchor. Stable per INTENT, not per attempt.
    idempotency_key   text NOT NULL,
    request_hash      text NULL,

    CONSTRAINT ck_line_amendment_successor CHECK ((action = 'Amend') = (new_line_id IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_line_amendment_idempotency
    ON orders.line_amendment (idempotency_key);
CREATE INDEX IF NOT EXISTS ix_line_amendment_line ON orders.line_amendment (order_line_id);
CREATE INDEX IF NOT EXISTS ix_line_amendment_order ON orders.line_amendment (order_id, amended_at DESC);

-- Append-only: the ledger of what was withdrawn must not itself be rewritable.
-- UPDATE only, for the reason above: the ledger's DELETE is withheld by the grant, and blocking the owner's
-- would leave the test suites unable to clean up after themselves for no security gain.
CREATE OR REPLACE FUNCTION orders.guard_line_amendment_append_only()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'orders.line_amendment is append-only (% attempted on %)', TG_OP, OLD.amendment_id
        USING ERRCODE = 'raise_exception';
END $$;

DROP TRIGGER IF EXISTS trg_line_amendment_append_only ON orders.line_amendment;
CREATE TRIGGER trg_line_amendment_append_only BEFORE UPDATE ON orders.line_amendment
    FOR EACH ROW EXECUTE FUNCTION orders.guard_line_amendment_append_only();

-- ---- Grants + tenant RLS (ADR-0011) -------------------------------------------------------------------------
-- No DELETE on the ledger: the trigger refuses it anyway, and withholding the privilege means a bug cannot try.
GRANT SELECT ON orders.amendment_reason TO hbmp_app;
GRANT SELECT, INSERT ON orders.line_amendment TO hbmp_app;

ALTER TABLE orders.line_amendment ENABLE ROW LEVEL SECURITY;
ALTER TABLE orders.line_amendment FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_line_amendment ON orders.line_amendment;
CREATE POLICY rls_line_amendment ON orders.line_amendment
    USING (tenant_id = current_setting('app.tenant_id', true));

COMMENT ON COLUMN orders.order_line.root_line_id IS
    'The first version of this line. Self on v1. Amendment creates a new row sharing the root, so "every '
    'version of this line" is one indexed query rather than a recursive walk re-derived at each call site.';
COMMENT ON TABLE orders.line_amendment IS
    'Append-only record of every applied cancel/amend, keyed by a UNIQUE idempotency key — the same '
    'duplicate-proof anchor as order_fulfillment. A double-tapped cancel applies once (design 46 §2).';
