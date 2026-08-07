-- ===========================================================================================================
-- A fourth kind of authorization request: extend the validity of something that has expired.
-- ===========================================================================================================
--
-- A pharmacist at the counter, or a lab/imaging technician, is holding a prescription or an order that has
-- gone past its window with the patient standing in front of them. Sending them back to a doctor for a
-- re-write is a wasted journey for a decision the approval team is already constituted to make.
--
-- WHY A SOURCE VALUE AND NOT A NEW AGGREGATE. The approval team's worklist, SLA clock, assignment,
-- append-only decision ledger, TAT reporting and audit already exist and are already the place these people
-- work. A parallel `validity_extension_request` table would have needed every one of them again, and would
-- have put half the team's decisions somewhere the other half's queue does not show.
--
-- `requesting_provider_id` stays mandatory for it (the CHECK below only exempts 'Manual'): the pharmacy or
-- the lab asking is exactly who the decision is about.

-- The drop-and-recreate below WIDENS the CHECK; it never narrows it. Every value the old constraint admitted
-- the new one still admits, so an instance still running the previous build keeps writing rows that pass —
-- which is what makes this safe in an expand-phase migration rather than a contract-phase one.
ALTER TABLE approvals.authorization DROP CONSTRAINT IF EXISTS authorization_source_check;  -- migrate-compat: contract-ok (widening a CHECK; the old value set stays valid for a previous-build instance)
ALTER TABLE approvals.authorization
    ADD CONSTRAINT authorization_source_check
    CHECK (source IN ('OrderLine','Prescription','Manual','ValidityExtension'));

COMMENT ON COLUMN approvals.authorization.source IS
    'Where the request came from. ValidityExtension = a pharmacist or technician asking for an expired '
    'prescription / investigation order to be made dispensable again; source_ref is that item''s id.';
