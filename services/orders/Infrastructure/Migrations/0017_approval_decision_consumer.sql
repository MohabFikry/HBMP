-- orders-service — 0017: what the approval-decision consumer needs — a dedupe ledger, and a reason code
-- for a line a reviewer did not authorise.
--
-- ============================================================================================================
-- WHY orders-service NEEDS ONE AT ALL, HAVING GONE THIRTY PHASES WITHOUT
-- ============================================================================================================
--
-- orders has always been a PUBLISHER. It emitted OrderPendingApproval and nothing came back: the 23 §2
-- transitions `PendingApproval → Approved` and `Approved → Active` were declared in OrderWorkflow and
-- executed by no code path in the platform. A gated order therefore stayed PendingApproval forever, and a
-- rejected one was indistinguishable from one still waiting.
--
-- The approval-decision consumer (ApprovalDecisionFeed) makes this service a CONSUMER for the first time, and
-- a consumer of an at-least-once transport needs somewhere to record what it has already applied. Without it
-- a redelivered AuthApproved would re-run the transition — harmless today, because the workflow guard refuses
-- Approved → Approved, but it would emit a second OrderApproved and a second audit row saying an approval
-- landed twice.
--
-- Intentionally RLS-FREE, exactly as policy.processed_event and eligibility.processed_event are: it holds
-- event ids and a timestamp, no tenant data, and it is written by a background consumer whose RLS session is
-- bound from the message envelope. A tenant policy on a table with no tenant column would only be a policy
-- that always fails.
--
-- Additive + idempotent (expand/contract). A previous-build instance neither reads nor writes it.

CREATE TABLE IF NOT EXISTS orders.processed_event (
    event_id     uuid PRIMARY KEY,
    processed_at timestamptz NOT NULL DEFAULT now()
);

GRANT SELECT, INSERT ON orders.processed_event TO hbmp_app;

COMMENT ON TABLE orders.processed_event IS
    'Transport-level dedupe ledger for the approval-decision consumer: a redelivered broker message is a '
    'no-op. No tenant data, so no RLS. Rows are never updated; they are only ever inserted and read.';

-- ------------------------------------------------------------------------------------------------------------
-- A REASON CODE FOR "THE APPROVAL TEAM DID NOT AUTHORISE THIS LINE".
--
-- `ck_order_line_amendment_attributed` requires a cancelled line to carry a reason code, and
-- `amendment_reason` is a closed vocabulary (a foreign key, not free text). Every code in it was written for a
-- CLINICIAN amending their own order — prescribing error, dose correction, patient declined. None of them
-- describes a partial approval, which is a new act: the item was clinically correct and the reviewer declined
-- to fund it.
--
-- `NotEligible` is the near miss and would have been the wrong answer. The patient may be perfectly eligible;
-- one item was refused. A cancelled line reading "patient not eligible" is a sentence somebody reads back in a
-- dispute, and it would be false.
--
-- Additive + idempotent. ON CONFLICT DO NOTHING, so re-running or applying over a database that already has it
-- is a no-op, and a previous-build instance that never writes this code is unaffected.
INSERT INTO orders.amendment_reason (code, name_en, name_ar, applies_to, sort_order) VALUES
    ('not-in-approved-scope', 'Not in approved scope', 'خارج نطاق الموافقة', 'All', 80)
ON CONFLICT (code) DO NOTHING;
