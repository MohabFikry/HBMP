-- 0006 — record WHERE a caller's identity was confirmed.
--
-- Caller identity is now confirmed by the agent ON THE PHONE; the platform records the attestation instead of
-- administering an on-screen challenge. Both kinds of row live in this table, and they mean different things:
-- an 'OnSystem' row means the platform checked ≥2 identifier types and accepted them, an 'OffSystem' row means
-- an agent said they confirmed the caller. Collapsing them would misreport what the platform did on a past call,
-- and this table is audit evidence rather than a cache.
--
-- DEFAULT 'OnSystem' is the point of the file: every row written before this column existed WAS an on-system
-- challenge, so the default states the truth about history rather than back-dating it into an attestation.
--
-- Expand-only and backward compatible: the running release neither writes nor reads the column.

ALTER TABLE callcentre.caller_verification
    ADD COLUMN IF NOT EXISTS method text NOT NULL DEFAULT 'OnSystem';

-- NOT VALID so the existing rows are not scanned: they already satisfy it via the default, and a validating
-- scan on a table this hot buys nothing. Run VALIDATE CONSTRAINT out of hours if a full check is ever wanted.
ALTER TABLE callcentre.caller_verification DROP CONSTRAINT IF EXISTS caller_verification_method_chk;
ALTER TABLE callcentre.caller_verification
    ADD CONSTRAINT caller_verification_method_chk
    CHECK (method IN ('OnSystem', 'OffSystem')) NOT VALID;
