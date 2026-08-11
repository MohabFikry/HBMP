-- reporting-service — 0004 the pending queue learns which authorization it is about. Additive + idempotent.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- `pending_authorization` is the current-state snapshot behind the pending-approvals report and the executive
-- dashboard's pending gauge. It carried the authorization's uuid, its priority, status and SLA clock — enough
-- to COUNT with, and nothing a person could act on.
--
-- That was sufficient while the only question was "how many". The oversight portal now asks the follow-up a
-- breach count always provokes: WHICH twelve. Answering it with a uuid would be answering it with a database
-- key, so the business number the rest of the platform prints on the request comes along, and so does the
-- reviewer holding it — "who has the three-day-old urgent case" is the first question after "how many".
--
-- STILL NO BENEFICIARY, and deliberately. This table feeds the de-identified reporting zone: an authorization
-- number, a priority and a reviewer are operational facts about a QUEUE, and adding the patient would turn an
-- aggregate read model into a clinical one behind an authorization check that was never designed to guard it.
-- A supervisor who needs the case opens it in approvals-service, with their own token and its own PHI audit.

ALTER TABLE reporting.pending_authorization ADD COLUMN IF NOT EXISTS auth_no     text;
ALTER TABLE reporting.pending_authorization ADD COLUMN IF NOT EXISTS reviewer_id text;

-- NULLABLE, not backfilled with a placeholder. Rows written before this migration were projected from events
-- that did carry `authNo`, but the projector had nowhere to put it — there is no way to recover it here
-- without replaying the feed, and a synthetic "unknown" would be indistinguishable from a real gap on screen.
-- A pending row with no number renders as its id and says so.

-- The breach list scans pending rows for this tenant and orders by age. Without an index that is a sequential
-- scan of every in-flight authorization on every dashboard load, which is the slow live query the reporting
-- read model exists to avoid.
CREATE INDEX IF NOT EXISTS ix_pending_tenant_due
    ON reporting.pending_authorization (tenant_id, sla_due_at);
