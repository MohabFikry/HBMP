-- pharmacy-service — 0004 bind the idempotency key to its request payload (phase 18.A3 / audit R2).
--
-- The dispense idempotency key was matched on its own, unbound to the request body: replaying a key
-- with a DIFFERENT quantity, batch or substituted drug silently returned the ORIGINAL dispense_event,
-- so a pharmacist who corrected a quantity and retried believed the correction had been dispensed when
-- nothing had changed. request_hash is a SHA-256 over the canonical dispense request; a replay whose
-- hash differs is now REJECTED.
--
-- Nullable + backfill-free: pre-existing rows carry NULL and behave exactly as before (additive).

ALTER TABLE pharmacy.dispense_event ADD COLUMN IF NOT EXISTS request_hash text;
