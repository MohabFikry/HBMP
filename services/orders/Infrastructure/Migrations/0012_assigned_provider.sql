-- orders-service — 0012 route an order to the provider that will DELIVER it, at the row level.
--
-- ON THE `migrate-compat: contract-ok` ACKNOWLEDGEMENTS BELOW.
-- Each marks a `DROP CONSTRAINT IF EXISTS ck_…` whose constraint this same migration adds immediately
-- afterwards. The DROP is idempotency boilerplate so the file can be re-run; on a first run the constraint
-- does not exist yet, and no previously deployed version can depend on one this migration introduces. That
-- is a different thing from dropping a constraint the running system relies on, which is what the gate is
-- for.
--
-- ============================================================================================================
-- THE DEFECT THIS EXISTS TO NOT REPEAT
-- ============================================================================================================
-- 29.2b / design 45 §2b. Audit R3 found that services/pharmacy/Api/DispensingGate.cs builds its ABAC resource
-- as `new ResourceRef { ProviderId = p.ProviderId }` — the CALLER's own provider id. The ownership rule then
-- compares the caller against themselves, which is always true, so any authenticated pharmacist holding
-- `pharmacy:read` browses the ENTIRE network queue. The class documentation says the rule enforces
-- provider-ownership; the code never had a row to own.
--
-- The reason it went unnoticed is worth stating: nothing failed. There is no error, no 500, no empty screen —
-- the queue simply contains other pharmacies' work, which looks exactly like a busy queue.
--
-- THE ROW MUST CARRY ITS OWNER. `ordering_provider_id` is who ASKED (a Mersal clinic); `order_fulfillment.
-- performing_provider_id` is who DID it, and is written after the fact — neither can scope a queue, because a
-- queue is a list of work NOT YET DONE. `assigned_provider_id` is the missing third: who this order was routed
-- TO. Without it the procedure portal would have had to answer "is this mine?" from the caller's own token,
-- which is the defect above, spelled the same way.
--
-- NULLABLE, and deliberately so. Lab and Radiology orders are fulfilled inside Mersal's own clinics and are
-- not routed to one named provider; forcing a value would mean inventing one. The GATE treats NULL as "not
-- yours" for an external provider (fail-closed) — see ProcedureQueueGate. A null owner is never "everyone's".

ALTER TABLE orders.investigation_order
    ADD COLUMN IF NOT EXISTS assigned_provider_id uuid NULL;

-- The queue query: one provider's undelivered work. Partial, because rows with no assignment are never in
-- any external provider's queue and indexing them would be indexing the answer "no".
CREATE INDEX IF NOT EXISTS ix_order_assigned_provider
    ON orders.investigation_order (assigned_provider_id, status)
    WHERE assigned_provider_id IS NOT NULL;

-- ============================================================================================================
-- CLINICAL CONTEXT IS AN EXPLICIT DISCLOSURE (design 45 §2b)
-- ============================================================================================================
-- A physiotherapist genuinely needs to know WHY they are treating someone. That is a clinician's deliberate
-- decision about one order, not a blanket grant of the EMR — so what travels is what the ordering doctor
-- CHOSE to send, stored as free text on the order rather than resolved from the encounter at read time.
--
-- Resolving it at read time is the tempting design and the wrong one: it would make the external provider's
-- view a live window onto a diagnosis that can change after the disclosure was made, and there would be no
-- record of what was actually disclosed. This column IS the record, and the audit event naming it is the
-- evidence that a named clinician chose to disclose it.
ALTER TABLE orders.investigation_order
    ADD COLUMN IF NOT EXISTS shared_clinical_context text NULL,
    ADD COLUMN IF NOT EXISTS shared_context_by       text NULL,
    ADD COLUMN IF NOT EXISTS shared_context_at       timestamptz NULL;

-- Context without an attributable author is not a disclosure, it is a leak with no one's name on it.
ALTER TABLE orders.investigation_order DROP CONSTRAINT IF EXISTS ck_order_shared_context_attributed;  -- migrate-compat: contract-ok (re-created below; see header)
ALTER TABLE orders.investigation_order
    ADD CONSTRAINT ck_order_shared_context_attributed CHECK (
        shared_clinical_context IS NULL
        OR (shared_context_by IS NOT NULL AND shared_context_at IS NOT NULL));

-- ============================================================================================================
-- LOOP CLOSURE (design 45 §2b)
-- ============================================================================================================
-- "Completion closes the loop: a report back to the ordering doctor, which for a REFERRAL is MANDATORY — an
-- open referral loop is the classic patient-safety failure in outpatient care."
--
-- Stored on the order rather than in a separate report table: there is exactly one completion report per
-- order, it is written once, and a child table would invite a second one. The attribution columns exist for
-- the same reason the disclosure ones do — a clinical statement with nobody's name on it is not a report.
ALTER TABLE orders.investigation_order
    ADD COLUMN IF NOT EXISTS completion_report      text NULL,
    ADD COLUMN IF NOT EXISTS completion_reported_by text NULL,
    ADD COLUMN IF NOT EXISTS completion_reported_at timestamptz NULL;

ALTER TABLE orders.investigation_order DROP CONSTRAINT IF EXISTS ck_order_completion_report_attributed;  -- migrate-compat: contract-ok (re-created below; see header)
ALTER TABLE orders.investigation_order
    ADD CONSTRAINT ck_order_completion_report_attributed CHECK (
        completion_report IS NULL
        OR (completion_reported_by IS NOT NULL AND completion_reported_at IS NOT NULL));

-- An empty report is an open loop wearing a closed one's clothes. Refused in the API with a clear message and
-- here as the backstop, because this column is what the doctor's "open loops" worklist counts as closed.
ALTER TABLE orders.investigation_order DROP CONSTRAINT IF EXISTS ck_order_completion_report_not_blank;  -- migrate-compat: contract-ok (re-created below; see header)
ALTER TABLE orders.investigation_order
    ADD CONSTRAINT ck_order_completion_report_not_blank CHECK (
        completion_report IS NULL OR length(btrim(completion_report)) > 0);
