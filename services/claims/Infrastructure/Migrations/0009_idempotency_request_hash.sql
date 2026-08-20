-- claims-service — 0009: bind every replayed Idempotency-Key to the request it replays, and give the
-- reimbursement channel a key at all.
--
-- ============================================================================================================
-- 1. THE REPLAYS WERE BODY-BLIND
-- ============================================================================================================
--
-- Decisions, submissions and adjustments each store the caller's key on the row they produce and look it up
-- on retry. None compared the BODY. So a key reused for a different request was answered with the earlier
-- one, 200 OK:
--
--   a DENY retried under a key already used to APPROVE returns the approval — the officer believes they
--   refused a line that is now payable, and no error exists to investigate;
--   an ADJUSTMENT reused across two amounts returns the first — the second correction never happens and the
--   batch total is wrong by the difference;
--   a SUBMISSION reused across two invoices returns the first claim — the provider is told their second
--   invoice was received when nothing was received.
--
-- 18.A3 settled the rule on consume and dispense: store a hash of the canonical request beside the key and
-- refuse a replay whose hash differs (`IdempotencyKeyRules`). These three tables never grew the column.
--
-- NULLABLE, and null means "unverifiable, so treated as a match". Rows written before this column cannot be
-- re-derived, and newly rejecting them would turn every in-flight retry into a 422 at deploy time for no
-- safety gain — the request they replay has already happened either way.

ALTER TABLE claims.claim_decision   ADD COLUMN IF NOT EXISTS request_hash text NULL;
ALTER TABLE claims.claim_adjustment ADD COLUMN IF NOT EXISTS request_hash text NULL;
ALTER TABLE claims.claim_submission ADD COLUMN IF NOT EXISTS request_hash text NULL;

COMMENT ON COLUMN claims.claim_decision.request_hash IS
    'SHA-256 of the canonical request this key produced (IdempotencyKeyRules.Hash). A replay whose hash '
    'differs is refused 422 rather than answered with the earlier decision. NULL on pre-0009 rows.';
COMMENT ON COLUMN claims.claim_adjustment.request_hash IS
    'SHA-256 of the canonical request this key produced. See claim_decision.request_hash.';
COMMENT ON COLUMN claims.claim_submission.request_hash IS
    'SHA-256 of the canonical request this key produced. See claim_decision.request_hash.';

-- ============================================================================================================
-- 2. REIMBURSEMENT HAD NO IDEMPOTENCY AT ALL
-- ============================================================================================================
--
-- `POST /api/v1/reimbursement-requests` is the only write in this service that neither required nor recorded
-- an `Idempotency-Key`. A retry — and this is the channel a BENEFICIARY submits through, from a phone, on a
-- connection that drops — created a second request over the same receipts. Both then run the OCR pipeline
-- and both can auto-match, so the same receipt can be reimbursed twice with nothing in either record hinting
-- that the other exists.
--
-- The key is stored on the request itself rather than in a side ledger, matching the three tables above: the
-- row it protects is the row that carries it, so there is no second thing to keep in step.
--
-- EXPAND ONLY: nullable, and the UNIQUE index is PARTIAL so the rows written before the column (which all
-- have NULL) do not collide with each other.

ALTER TABLE claims.reimbursement_request
    ADD COLUMN IF NOT EXISTS idempotency_key text NULL,
    ADD COLUMN IF NOT EXISTS request_hash    text NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_reimbursement_request_idempotency
    ON claims.reimbursement_request (tenant_id, idempotency_key)
    WHERE idempotency_key IS NOT NULL;

COMMENT ON COLUMN claims.reimbursement_request.idempotency_key IS
    'The caller''s Idempotency-Key. Required from 0009 onward; NULL on rows submitted before it, which is '
    'why the unique index is partial. A retry from a beneficiary''s phone on a dropped connection used to '
    'create a second request over the same receipts, and both could auto-match.';
