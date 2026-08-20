-- pharmacy-service — 0021: notes on a prescription line (design 46 §7b).
--
-- Doc 46 §7b is titled "Notes on prescriptions, labs, radiology and procedures" and opens with "Every order
-- line gains notes". orders-service built the whole model in 30.5b — orders.order_note, three visibility
-- classes, sensitivity inherited from the line, append-only with a cancel that never deletes. pharmacy never
-- got it, so the order kind the doc names FIRST is the one with nowhere to put "patient cannot swallow
-- tablets — syrup if available".
--
-- THE SAME MODEL ON A DIFFERENT SUBJECT, deliberately, and the doc says why: "A second notes mechanism means
-- two behaviours for 'cancel a note' and two answers to 'who can read this'." Column-for-column with
-- orders.order_note, and the vocabulary (NoteVisibility, NoteReader, NoteAudience) is the shared one in
-- libs/amendment rather than a copy.
--
-- KEYED ON root_line_id, not on the line id. A note is written about the clinical intent, and that intent
-- survives an amendment: 30.1 supersedes a line rather than mutating it, so a note written on v1 must stay
-- visible on v2 or every amendment would silently discard the instructions attached to it.
CREATE TABLE IF NOT EXISTS pharmacy.prescription_note (
    note_id             uuid PRIMARY KEY,
    tenant_id           text NOT NULL,
    subject_type        text NOT NULL DEFAULT 'PrescriptionLine',
    subject_id          uuid NOT NULL,
    root_line_id        uuid NOT NULL,
    visibility          text NOT NULL,
    body                text NOT NULL,
    author_user_id      uuid NOT NULL,
    author_display_name text NOT NULL,
    authored_at         timestamptz NOT NULL,
    status              text NOT NULL DEFAULT 'Active',
    cancelled_by        uuid,
    cancelled_at        timestamptz,
    cancel_reason       text,
    CONSTRAINT ck_rx_note_visibility CHECK (visibility IN ('ToFulfiller','Internal','FromFulfiller')),
    CONSTRAINT ck_rx_note_status CHECK (status IN ('Active','Cancelled')),
    -- A cancellation is three facts or none: when, by whom, and why. Half of them is a note that was
    -- withdrawn with no way to say what happened.
    CONSTRAINT ck_rx_note_cancel_complete CHECK (
        (status = 'Cancelled') = (cancelled_at IS NOT NULL AND cancelled_by IS NOT NULL AND cancel_reason IS NOT NULL)
    ),
    -- 500 characters, matching orders. A note is an operational instruction; the cap is what stops a
    -- free-text box on an order from becoming a clinical record nobody's EMR can see.
    CONSTRAINT ck_rx_note_length CHECK (char_length(body) <= 500),
    CONSTRAINT ck_rx_note_tenant_not_blank CHECK (tenant_id <> '')
);

CREATE INDEX IF NOT EXISTS ix_rx_note_root ON pharmacy.prescription_note (root_line_id, authored_at DESC);

COMMENT ON TABLE pharmacy.prescription_note IS
    'Operational notes on a prescription line (design 46 §7b). The orders.order_note model on a different '
    'subject. Append-only: cancelling marks, never deletes.';

ALTER TABLE pharmacy.prescription_note ENABLE ROW LEVEL SECURITY;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_policies
                   WHERE schemaname = 'pharmacy' AND tablename = 'prescription_note'
                     AND policyname = 'rx_note_tenant_isolation') THEN
        CREATE POLICY rx_note_tenant_isolation ON pharmacy.prescription_note
            USING (tenant_id = current_setting('hbmp.tenant_id', true))
            WITH CHECK (tenant_id = current_setting('hbmp.tenant_id', true));
    END IF;
END $$;

GRANT SELECT, INSERT, UPDATE ON pharmacy.prescription_note TO hbmp_app;
