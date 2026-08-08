-- pharmacy-service — 0013 amend & cancel a SIGNED prescription, at LINE level (phase 30 Gate 1, design 46 §1/§3).
--
-- The medication twin of orders 0013. Read that file's header for the reasoning; it is not repeated here,
-- because two copies of an argument drift and then disagree. What follows is what differs.
--
-- ============================================================================================================
-- WHAT DIFFERS FROM THE ORDERS SIDE
-- ============================================================================================================
-- 1. `Superseded` is added to prescription_line's CHECK only, NOT to prescription's — same reasoning as
--    orders: a prescription with one superseded line and two live ones is not superseded, and a status
--    nothing can enter is one somebody eventually sets by hand.
--
-- 2. A chronic line owns `prescription_dispense_window` rows (0012). Superseding such a line moves its
--    UNDISPENSED windows to the successor and leaves the dispensed ones attached to the original, because a
--    collected window is a fact about the row that was collected against. That is Gate 3's work; the schema
--    here only has to make it expressible, which root_line_id does.
--
-- 3. The frozen set includes `duration_days` and `refills_allowed`. Both are clinical content: changing
--    either changes the medication the patient receives.
--
-- Additive + idempotent.

-- ============================================================================================================
-- 1. The coded reason vocabulary — IDENTICAL to orders.amendment_reason
-- ============================================================================================================
-- Two copies, not one shared table, so the FK is real and cancelling a prescription never depends on another
-- service being reachable. AmendmentReasonSeedTests reads both migrations and fails the build if they drift —
-- which is the failure mode a copy has, made loud instead of silent.

CREATE TABLE IF NOT EXISTS pharmacy.amendment_reason (
    code        varchar(32) PRIMARY KEY,
    name_en     text        NOT NULL,
    name_ar     text        NOT NULL,
    applies_to  varchar(16) NOT NULL DEFAULT 'All'
                CHECK (applies_to IN ('All','Prescription','Order')),
    is_active   boolean     NOT NULL DEFAULT true,
    sort_order  int         NOT NULL DEFAULT 0,
    created_at  timestamptz NOT NULL DEFAULT now()
);

INSERT INTO pharmacy.amendment_reason (code, name_en, name_ar, applies_to, sort_order) VALUES
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

ALTER TABLE pharmacy.prescription_line
    ADD COLUMN IF NOT EXISTS version_no             int  NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS supersedes_id          uuid NULL REFERENCES pharmacy.prescription_line(prescription_line_id),
    ADD COLUMN IF NOT EXISTS superseded_by_id       uuid NULL REFERENCES pharmacy.prescription_line(prescription_line_id),
    ADD COLUMN IF NOT EXISTS root_line_id           uuid NULL,
    ADD COLUMN IF NOT EXISTS amendment_reason_code  varchar(32)  NULL REFERENCES pharmacy.amendment_reason(code),
    ADD COLUMN IF NOT EXISTS amendment_reason_text  varchar(300) NULL,
    ADD COLUMN IF NOT EXISTS amended_by             uuid NULL,
    ADD COLUMN IF NOT EXISTS amended_at             timestamptz NULL;

UPDATE pharmacy.prescription_line SET root_line_id = prescription_line_id WHERE root_line_id IS NULL;
-- NOT NULL is DEFERRED to deferred/0014, not applied here. During a rolling deploy an OLD replica
-- still inserts prescription_line rows without root_line_id, and a NOT NULL on this column would turn that into a
-- constraint violation — i.e. a prescription a doctor cannot write, mid-encounter. Expand now, contract
-- once the switch deploy has fully rolled out (the same discipline as the radiology rename).
-- The new code fills it at the DbContext choke point, so no new row is ever written without it.

CREATE INDEX IF NOT EXISTS ix_rx_line_root ON pharmacy.prescription_line (root_line_id, version_no);
CREATE INDEX IF NOT EXISTS ix_rx_line_superseded_by ON pharmacy.prescription_line (superseded_by_id)
    WHERE superseded_by_id IS NOT NULL;

DO $$  -- migrate-compat: contract-ok (WIDENS the status CHECK to admit 'Superseded'; the old, narrower constraint is replaced in the same migration, so no value that was legal before becomes illegal)
DECLARE c record;
BEGIN
    FOR c IN SELECT conname FROM pg_constraint
             WHERE conrelid = 'pharmacy.prescription_line'::regclass AND contype = 'c'
               AND pg_get_constraintdef(oid) LIKE '%status%'
    LOOP
        EXECUTE format('ALTER TABLE pharmacy.prescription_line DROP CONSTRAINT %I', c.conname);
    END LOOP;
END $$;

ALTER TABLE pharmacy.prescription_line
    ADD CONSTRAINT ck_rx_line_status CHECK (
        status IN ('Active','PartiallyDispensed','Dispensed','Cancelled','Superseded'));

-- NOT VALID for the same reason orders 0013 gives: rows cancelled before this migration carry no reason, and
-- inventing one for them would be a worse lie than the gap. Enforced on every write from here on.
ALTER TABLE pharmacy.prescription_line
    ADD CONSTRAINT ck_rx_line_amendment_attributed CHECK (
        status NOT IN ('Cancelled','Superseded')
        OR (amendment_reason_code IS NOT NULL AND amended_by IS NOT NULL AND amended_at IS NOT NULL)
    ) NOT VALID;

ALTER TABLE pharmacy.prescription_line
    ADD CONSTRAINT ck_rx_line_superseded_has_successor CHECK (
        (status = 'Superseded') = (superseded_by_id IS NOT NULL));

-- ============================================================================================================
-- 3. NOTHING SIGNED IS MUTATED — enforced by the database
-- ============================================================================================================
-- quantity_dispensed and status stay updatable: that is the accumulator moving forward, not the prescription
-- changing. Everything that says WHAT DRUG, HOW MUCH, HOW OFTEN and FOR HOW LONG is frozen.

-- No DELETE branch: "nothing is deleted, ever" is enforced by REVOKING the privilege from hbmp_app below —
-- see orders 0013 for the full argument. Blocking it here would additionally block the schema owner, whose
-- deletes are maintenance rather than application traffic.
CREATE OR REPLACE FUNCTION pharmacy.guard_rx_line_signed()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.prescription_id     IS DISTINCT FROM NEW.prescription_id
       OR OLD.drug_id          IS DISTINCT FROM NEW.drug_id
       OR OLD.drug_name        IS DISTINCT FROM NEW.drug_name
       OR OLD.dose             IS DISTINCT FROM NEW.dose
       OR OLD.route            IS DISTINCT FROM NEW.route
       OR OLD.frequency        IS DISTINCT FROM NEW.frequency
       OR OLD.quantity_prescribed IS DISTINCT FROM NEW.quantity_prescribed
       OR OLD.duration_days    IS DISTINCT FROM NEW.duration_days
       OR OLD.refills_allowed  IS DISTINCT FROM NEW.refills_allowed THEN
        RAISE EXCEPTION
            'prescription line % is signed clinical content and can never be edited in place — supersede it (design 46 §1)',
            OLD.prescription_line_id USING ERRCODE = 'raise_exception';
    END IF;

    IF OLD.version_no IS DISTINCT FROM NEW.version_no
       OR OLD.supersedes_id IS DISTINCT FROM NEW.supersedes_id
       OR OLD.root_line_id  IS DISTINCT FROM NEW.root_line_id THEN
        RAISE EXCEPTION 'the version chain of line % is immutable', OLD.prescription_line_id
            USING ERRCODE = 'raise_exception';
    END IF;

    IF OLD.status IN ('Cancelled','Superseded') AND NEW.status IS DISTINCT FROM OLD.status THEN
        RAISE EXCEPTION 'line % is %; it cannot be reinstated — write a new prescription',
            OLD.prescription_line_id, OLD.status USING ERRCODE = 'raise_exception';
    END IF;

    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_rx_line_signed ON pharmacy.prescription_line;
CREATE TRIGGER trg_rx_line_signed BEFORE UPDATE ON pharmacy.prescription_line
    FOR EACH ROW EXECUTE FUNCTION pharmacy.guard_rx_line_signed();

-- NOTHING IS DELETED, EVER (design 46 §1, invariant 1). 0003 granted DELETE across the schema; these two are
-- the clinical record, so the grant is withdrawn from the runtime role here.
REVOKE DELETE ON pharmacy.prescription_line FROM hbmp_app;
REVOKE DELETE ON pharmacy.prescription FROM hbmp_app;

-- ============================================================================================================
-- 4. The amendment ledger
-- ============================================================================================================

CREATE TABLE IF NOT EXISTS pharmacy.line_amendment (
    amendment_id       uuid PRIMARY KEY,
    tenant_id          text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',
    prescription_id    uuid NOT NULL REFERENCES pharmacy.prescription(prescription_id),
    prescription_line_id uuid NOT NULL REFERENCES pharmacy.prescription_line(prescription_line_id),
    new_line_id        uuid NULL REFERENCES pharmacy.prescription_line(prescription_line_id),

    action             varchar(10) NOT NULL CHECK (action IN ('Cancel','Amend')),
    from_status        varchar(20) NOT NULL,
    to_status          varchar(20) NOT NULL,

    reason_code        varchar(32)  NOT NULL REFERENCES pharmacy.amendment_reason(code),
    reason_text        varchar(300) NULL,

    amended_by         uuid NOT NULL,
    amended_by_display varchar(200) NULL,
    amended_at         timestamptz NOT NULL DEFAULT now(),

    idempotency_key    text NOT NULL,
    request_hash       text NULL,

    CONSTRAINT ck_rx_line_amendment_successor CHECK ((action = 'Amend') = (new_line_id IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_rx_line_amendment_idempotency
    ON pharmacy.line_amendment (idempotency_key);
CREATE INDEX IF NOT EXISTS ix_rx_line_amendment_line ON pharmacy.line_amendment (prescription_line_id);
CREATE INDEX IF NOT EXISTS ix_rx_line_amendment_rx ON pharmacy.line_amendment (prescription_id, amended_at DESC);

CREATE OR REPLACE FUNCTION pharmacy.guard_line_amendment_append_only()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'pharmacy.line_amendment is append-only (% attempted on %)', TG_OP, OLD.amendment_id
        USING ERRCODE = 'raise_exception';
END $$;

DROP TRIGGER IF EXISTS trg_rx_line_amendment_append_only ON pharmacy.line_amendment;
CREATE TRIGGER trg_rx_line_amendment_append_only BEFORE UPDATE ON pharmacy.line_amendment
    FOR EACH ROW EXECUTE FUNCTION pharmacy.guard_line_amendment_append_only();

-- ---- Grants + tenant RLS (ADR-0011) -------------------------------------------------------------------------
GRANT SELECT ON pharmacy.amendment_reason TO hbmp_app;
GRANT SELECT, INSERT ON pharmacy.line_amendment TO hbmp_app;

ALTER TABLE pharmacy.line_amendment ENABLE ROW LEVEL SECURITY;
ALTER TABLE pharmacy.line_amendment FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_line_amendment ON pharmacy.line_amendment;
CREATE POLICY rls_line_amendment ON pharmacy.line_amendment
    USING (tenant_id = current_setting('app.tenant_id', true));

COMMENT ON TABLE pharmacy.line_amendment IS
    'Append-only record of every applied cancel/amend, keyed by a UNIQUE idempotency key — the same '
    'duplicate-proof anchor as dispense_event. A double-tapped cancel applies once (design 46 §2).';
