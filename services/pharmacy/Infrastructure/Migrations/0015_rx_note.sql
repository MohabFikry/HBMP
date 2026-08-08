-- pharmacy-service — 0015 notes on a prescription line (phase 30 Gate 5b, design 46 §7b).
--
-- ⚠ NOT YET WIRED. This table is created and correct, and NOTHING IN pharmacy-service WRITES OR READS IT
-- YET — the entity, the endpoints and the projection are orders-side only as of 30.5b. That is the same
-- shape of gap phase 29's chronic prescribing turned out to have (docs/phase-30-gate-3-notes.md), so it is
-- stated here rather than left to be discovered: an empty table with a trigger and an RLS policy looks
-- finished from every angle except the one that matters.
--
-- The medication twin of orders 0015. Read that file's header for the reasoning — it is the doc-38 notes
-- model (policy 0009) on a different subject, per owning service so the FK is real and writing a note never
-- depends on another service being reachable. What follows is the same shape on prescription_line.
--
-- ============================================================================================================
-- THIS IS THE DOC-38 NOTES MODEL, ON A DIFFERENT SUBJECT
-- ============================================================================================================
-- policy.note (policy 0009) already defines it: append-only enforced by a TRIGGER rather than by the API
-- returning 409; the author SNAPSHOTTED at write time rather than joined; cancellable with a mandatory reason
-- and never deletable; visibility that may be RAISED but never lowered. Every one of those properties is
-- reproduced here, deliberately, rather than re-decided.
--
-- Design 46 §7b: "Do NOT write a fourth notes implementation: two mechanisms means two behaviours for
-- 'cancel a note' and two answers to 'who can read this'."
--
-- ============================================================================================================
-- PER OWNING SERVICE, NOT ONE SHARED TABLE
-- ============================================================================================================
-- The prompt leaves this open. Per service, for the reason amendment_reason is per service (0013): the FK to
-- the line must be REAL, and writing a note must not depend on another service being reachable — a pharmacist
-- typing "sample haemolysed, please repeat" during an outage is exactly when the note matters most. Recorded
-- in ADR-0030.
--
-- ============================================================================================================
-- A NOTE IS NOT AN AMENDMENT
-- ============================================================================================================
-- Nothing here touches order_line. Adding a note does not supersede the order, does not bump version_no and
-- does not invalidate an authorisation — conflating them would send every "fasting sample" back to the
-- approval queue. The separation is structural: notes live in their own table and the amendment path never
-- reads them.
--
-- Additive + idempotent.

CREATE TABLE IF NOT EXISTS pharmacy.rx_note (
    note_id             uuid PRIMARY KEY,
    tenant_id           text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',

    -- The subject, as a (type, id) VALUE rather than one nullable FK per kind — the shape policy.note uses,
    -- so a third subject arriving needs no schema change. The FK is still real for the kind this service owns.
    subject_type        varchar(24) NOT NULL CHECK (subject_type IN ('PrescriptionLine')),
    subject_id          uuid NOT NULL REFERENCES pharmacy.prescription_line(prescription_line_id),
    -- 30.1's chain: a note written on v1 must stay visible on v2, because it is about the clinical intent,
    -- which survives an amendment. Reads resolve by root, writes record the line the author was looking at.
    root_line_id        uuid NOT NULL,

    -- THREE CLASSES, because the reader differs (design 46 §7b).
    --   ToFulfiller   clinician -> the pharmacy/lab/radiology/centre holding the order, + internal clinical
    --   Internal      clinician -> internal clinical roles ONLY. THE EXTERNAL PROVIDER NEVER SEES THIS.
    --   FromFulfiller provider  -> the ordering clinician + internal clinical roles
    -- Default ToFulfiller: the common case is an instruction meant to be read.
    visibility          varchar(16) NOT NULL DEFAULT 'ToFulfiller'
                        CHECK (visibility IN ('ToFulfiller','Internal','FromFulfiller')),

    -- LENGTH-CAPPED on purpose. "A note is not a clinical record and must not become one": a free-text box on
    -- an order attracts clinical findings, and anything written there sits outside the EMR, outside the
    -- sensitivity classification, and outside the record the next clinician reads. The cap is the structural
    -- half of that; the helper text at the point of writing is the other half.
    body                varchar(500) NOT NULL CHECK (length(btrim(body)) > 0),

    -- The signature, SNAPSHOTTED. Never a join to identity: a note written in 2026 must still show who wrote
    -- it after that person is renamed, moves team or is de-provisioned. A join would quietly rewrite the
    -- signature, or lose it entirely (policy 0009's argument, unchanged).
    author_user_id      uuid NOT NULL,
    author_display_name varchar(200) NOT NULL,
    authored_at         timestamptz NOT NULL DEFAULT now(),

    status              varchar(12) NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Cancelled')),
    cancelled_by        uuid NULL,
    cancelled_at        timestamptz NULL,
    cancel_reason       varchar(300) NULL,

    -- A cancellation without who, when and why is not a cancellation — it is a note that changed state for
    -- reasons nobody recorded. Cancelling keeps the note VISIBLE, struck through: "there was a note here and
    -- it was withdrawn, by X, on Y, because Z" is information; a gap is not.
    CONSTRAINT ck_rx_note_cancellation_complete CHECK (
        status <> 'Cancelled'
        OR (cancelled_by IS NOT NULL AND cancelled_at IS NOT NULL
            AND cancel_reason IS NOT NULL AND length(btrim(cancel_reason)) > 0)),
    CONSTRAINT ck_rx_note_active_is_clean CHECK (
        status <> 'Active'
        OR (cancelled_by IS NULL AND cancelled_at IS NULL AND cancel_reason IS NULL))
);

CREATE INDEX IF NOT EXISTS ix_rx_note_subject ON pharmacy.rx_note (root_line_id, authored_at DESC);
CREATE INDEX IF NOT EXISTS ix_rx_note_line ON pharmacy.rx_note (subject_id);

-- ---- Append-only, enforced by the database -----------------------------------------------------------------
-- The API answers a body edit with 409, but that is not the invariant: a repair script, a future endpoint or a
-- psql session walks straight past it.
CREATE OR REPLACE FUNCTION pharmacy.guard_rx_note_append_only()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.body IS DISTINCT FROM NEW.body THEN
        RAISE EXCEPTION 'note % is append-only: its body can never be edited — write a new note', OLD.note_id
            USING ERRCODE = 'raise_exception';
    END IF;

    IF OLD.author_user_id IS DISTINCT FROM NEW.author_user_id
       OR OLD.author_display_name IS DISTINCT FROM NEW.author_display_name
       OR OLD.authored_at IS DISTINCT FROM NEW.authored_at THEN
        RAISE EXCEPTION 'note % is signed: its author and timestamp can never be changed', OLD.note_id
            USING ERRCODE = 'raise_exception';
    END IF;

    IF OLD.subject_type IS DISTINCT FROM NEW.subject_type
       OR OLD.subject_id IS DISTINCT FROM NEW.subject_id
       OR OLD.root_line_id IS DISTINCT FROM NEW.root_line_id THEN
        RAISE EXCEPTION 'note % is append-only: what it is about cannot be reassigned', OLD.note_id
            USING ERRCODE = 'raise_exception';
    END IF;

    -- Visibility may be RAISED, never lowered. Lowering it would retroactively expose a clinician's internal
    -- reasoning to the external provider it was deliberately withheld from — the same discipline policy.note
    -- and documents both follow.
    IF OLD.visibility IS DISTINCT FROM NEW.visibility
       AND pharmacy.note_visibility_rank(NEW.visibility) < pharmacy.note_visibility_rank(OLD.visibility) THEN
        RAISE EXCEPTION 'note % visibility can be raised but never lowered (% → %)',
            OLD.note_id, OLD.visibility, NEW.visibility USING ERRCODE = 'raise_exception';
    END IF;

    IF OLD.status = 'Cancelled' AND NEW.status <> 'Cancelled' THEN
        RAISE EXCEPTION 'note % is cancelled and cannot be reinstated — write a new note', OLD.note_id
            USING ERRCODE = 'raise_exception';
    END IF;

    RETURN NEW;
END $$;

-- ToFulfiller is the WIDEST audience (an external provider plus internal clinical roles), so it ranks lowest;
-- Internal withholds the note from that provider and ranks highest. FromFulfiller sits between: the ordering
-- clinician and internal roles, but not the wider fulfilling side.
CREATE OR REPLACE FUNCTION pharmacy.note_visibility_rank(v text)
RETURNS int LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE v WHEN 'ToFulfiller' THEN 1 WHEN 'FromFulfiller' THEN 2 WHEN 'Internal' THEN 3 ELSE 0 END
$$;

DROP TRIGGER IF EXISTS trg_rx_note_append_only ON pharmacy.rx_note;
CREATE TRIGGER trg_rx_note_append_only BEFORE UPDATE ON pharmacy.rx_note
    FOR EACH ROW EXECUTE FUNCTION pharmacy.guard_rx_note_append_only();

-- ---- Grants + tenant RLS (ADR-0011) -------------------------------------------------------------------------
-- No DELETE grant: cancellable but NEVER deletable, and withholding the privilege means a bug cannot attempt it.
GRANT SELECT, INSERT, UPDATE ON pharmacy.rx_note TO hbmp_app;
REVOKE DELETE ON pharmacy.rx_note FROM hbmp_app;

ALTER TABLE pharmacy.rx_note ENABLE ROW LEVEL SECURITY;
ALTER TABLE pharmacy.rx_note FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_rx_note ON pharmacy.rx_note;
CREATE POLICY rls_rx_note ON pharmacy.rx_note
    USING (tenant_id = current_setting('app.tenant_id', true));

COMMENT ON COLUMN pharmacy.rx_note.body IS
    'An OPERATIONAL INSTRUCTION, capped at 500 characters. Clinical findings belong in the encounter note: '
    'anything written here sits outside the EMR, outside the sensitivity classification and outside the '
    'record the next clinician reads — they open the encounter and never see it (design 46 §7b).';
