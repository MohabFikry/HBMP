-- approvals-service — 0011: bind a replayed Idempotency-Key to the request it replays.
--
-- ============================================================================================================
-- A REJECT RETRIED UNDER AN APPROVE'S KEY RETURNED THE APPROVAL, 200 OK
-- ============================================================================================================
--
-- `Decisions.Decide` looks the key up in `processed_request` and, on a hit, returns the decision that key
-- already produced. It never compared the BODY. So a client that reused a key across two different decisions
-- — the same reviewer correcting themselves, a retry after a UI reload, a script with a fixed key — was told
-- "approved" for a request it had just tried to reject, with a 200 and nothing anywhere recording the
-- disagreement. The authorization really is approved; the reviewer believes they rejected it.
--
-- 18.A3 established the fix for exactly this on the consume and dispense paths: store a hash of the canonical
-- request beside the key and refuse a replay whose hash differs (`IdempotencyKeyRules`). Those paths have had
-- `request_hash` since; the approvals ledger never grew the column, so the rule could not be applied here.
--
-- NULLABLE, and null means "unverifiable, so treated as a match". Rows written before this column exist and
-- cannot be re-derived; newly rejecting them would turn every in-flight retry into a 422 at deploy time for
-- no safety gain — the request they replay has already happened either way. `IdempotencyKeyRules.Matches`
-- encodes that reading in one place.
--
-- Additive + idempotent (expand/contract). A previous-build instance neither reads nor writes it.

ALTER TABLE approvals.processed_request
    ADD COLUMN IF NOT EXISTS request_hash text NULL;

COMMENT ON COLUMN approvals.processed_request.request_hash IS
    'SHA-256 of the canonical request this key produced (IdempotencyKeyRules.Hash). A replay whose hash '
    'differs is refused 422 rather than answered with the earlier decision. NULL on rows written before '
    'this column: unverifiable, so treated as a match.';
