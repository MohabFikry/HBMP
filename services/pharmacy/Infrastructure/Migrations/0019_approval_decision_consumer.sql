-- pharmacy-service — 0019: what the approval-decision consumer needs — a dedupe ledger, and a reason code
-- for a line a reviewer did not authorise.
--
-- The medication twin of orders 0017, and the gap it closes was the sharper of the two. pharmacy declared
-- `Submitted → Approved` in PrescriptionWorkflow and `IsDispensable` admits only Approved and
-- PartiallyDispensed — but the ONLY path that ever set a prescription Approved was the auto-route at
-- creation, for scripts that needed no approval at all. So a prescription that WAS sent for approval could
-- never become dispensable, whatever the reviewer decided: the counter refused it, correctly, forever.
--
-- The approval-decision consumer (ApprovalDecisionFeed) makes this service a consumer of an at-least-once
-- transport for the first time, and it needs somewhere to record what it has already applied.
--
-- Intentionally RLS-FREE, exactly as policy.processed_event is: event ids and a timestamp, no tenant data,
-- written by a background consumer whose RLS session is bound from the message envelope.
--
-- Additive + idempotent (expand/contract). A previous-build instance neither reads nor writes it.

CREATE TABLE IF NOT EXISTS pharmacy.processed_event (
    event_id     uuid PRIMARY KEY,
    processed_at timestamptz NOT NULL DEFAULT now()
);

GRANT SELECT, INSERT ON pharmacy.processed_event TO hbmp_app;

COMMENT ON TABLE pharmacy.processed_event IS
    'Transport-level dedupe ledger for the approval-decision consumer: a redelivered broker message is a '
    'no-op. No tenant data, so no RLS. Rows are never updated; they are only ever inserted and read.';

-- ------------------------------------------------------------------------------------------------------------
-- A REASON CODE FOR "THE APPROVAL TEAM DID NOT AUTHORISE THIS LINE".
--
-- `ck_rx_line_amendment_attributed` requires a cancelled line to carry a reason code, and
-- `amendment_reason` is a closed vocabulary (a foreign key, not free text). Every code in it was written for a
-- CLINICIAN amending their own prescription — prescribing error, dose correction, patient declined. None of them
-- describes a partial approval, which is a new act: the item was clinically correct and the reviewer declined
-- to fund it.
--
-- `NotEligible` is the near miss and would have been the wrong answer. The patient may be perfectly eligible;
-- one item was refused. A cancelled line reading "patient not eligible" is a sentence somebody reads back in a
-- dispute, and it would be false.
--
-- Additive + idempotent. ON CONFLICT DO NOTHING, so re-running or applying over a database that already has it
-- is a no-op, and a previous-build instance that never writes this code is unaffected.
INSERT INTO pharmacy.amendment_reason (code, name_en, name_ar, applies_to, sort_order) VALUES
    ('not-in-approved-scope', 'Not in approved scope', 'خارج نطاق الموافقة', 'All', 80)
ON CONFLICT (code) DO NOTHING;
