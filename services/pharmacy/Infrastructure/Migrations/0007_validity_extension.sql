-- pharmacy-service — 0007 record WHICH approval put an expired prescription back in date. Additive.
--
-- Two columns, and the first one is not bookkeeping: `validity_extended_by` is what makes the apply
-- idempotent. approvals calls this service to action an approved extension and retries on a timeout; without
-- a record of which authorization has already been applied, a retry stacks a second full validity period on
-- top of the first, and the prescription quietly becomes valid for twice as long as anyone decided.
--
-- It is also the answer to "why is this prescription still dispensable" — the expiry alone cannot say
-- whether it was issued that way or extended, and by whose decision.

ALTER TABLE pharmacy.prescription
    ADD COLUMN IF NOT EXISTS validity_extended_by uuid,
    ADD COLUMN IF NOT EXISTS validity_extended_at  timestamptz;

COMMENT ON COLUMN pharmacy.prescription.validity_extended_by IS
    'The approvals authorization that revalidated this prescription. Also the idempotency key for the apply: '
    'a retried callback for the same authorization must not grant a second period.';
