-- orders-service — 0016: the OP-Procedure KIND and its SESSION COUNT move to the ORDER, and a line's
-- quantity becomes a quantity PER SESSION.
--
-- ============================================================================================================
-- THIS REVERSES A PUBLISHED DESIGN DECISION, DELIBERATELY
-- ============================================================================================================
--
-- Design 45 §2 says "sessions ARE the quantity, never a parallel counter", and 29.2 built it that way: the
-- kind sat on each LINE and a session-based line's `quantity_ordered` was its session count. That model
-- cannot express the thing an outpatient procedure order actually is.
--
-- A physiotherapy course is ONE clinical decision — one kind, one number of sessions — that may involve
-- several billable items per attendance. Under the old model each line carried its own type and its own
-- session count, so a two-item course could be composed as six sessions of one item and eight of the other,
-- which is not a course any centre can deliver; and there was nowhere at all to say "three of these per
-- session", because the quantity slot was already spent on the session count.
--
-- So:
--   order.procedure_type_code   the KIND — one per order, because it is one decision
--   order.sessions              the COURSE LENGTH — one per order, for the same reason
--   line.quantity_per_session   how much of THIS item at each attendance
--   line.quantity_ordered       UNCHANGED IN MEANING: the metered total, now = sessions x per-session
--
-- The last line is what makes this affordable. `quantity_ordered` remains the number consume meters against,
-- approvals narrow, and the delivering centre's queue counts down — so the atomic consume path, the
-- partial-approval arithmetic and the provider projection all keep working untouched. Sessions delivered is
-- derived (`quantity_consumed / quantity_per_session`) rather than stored, because a stored second counter is
-- exactly the "parallel counter" design 45 §2 was right to forbid.
--
-- ============================================================================================================
-- EXPAND ONLY
-- ============================================================================================================
-- Every column is nullable or defaulted, and no existing column changes type or meaning. Rows written before
-- this migration have `quantity_per_session = 1`, under which `sessions x per-session` equals the session
-- count they already stored — so the old data reads correctly under the new rule without being rewritten.
-- The line-level `procedure_type_code` is LEFT IN PLACE and still written, so a rollback to the previous
-- build finds the data it expects. Dropping it is a later contract step, once nothing reads it.

ALTER TABLE orders.investigation_order
    ADD COLUMN IF NOT EXISTS procedure_type_code varchar(64) NULL,
    ADD COLUMN IF NOT EXISTS sessions            integer NULL;

COMMENT ON COLUMN orders.investigation_order.procedure_type_code IS
    '31.1 — the OP-Procedure KIND (masterdata.procedure_type), one per ORDER because it is one clinical '
    'decision. NULL on Lab and Radiology orders. Validated on the write path against every line''s CPT '
    'section: an unvalidated type field is decorative, and every report built on it is quietly wrong.';

COMMENT ON COLUMN orders.investigation_order.sessions IS
    '31.1 — the COURSE LENGTH in attendances. NULL when the procedure type is not session-based, which is a '
    'different fact from 1 and must not be stored as one. Sessions AUTHORISED is derived from '
    'quantity_ordered, which approvals narrow — never from this, which is what was REQUESTED.';

-- A session count of zero is not a course; it is an order that entitles the beneficiary to nothing, which
-- must be expressed by not raising the order at all.
ALTER TABLE orders.investigation_order DROP CONSTRAINT IF EXISTS ck_order_sessions_positive;  -- migrate-compat: contract-ok (idempotent re-run guard for a constraint this same migration creates immediately below; it has never existed on a previously-deployed build)
ALTER TABLE orders.investigation_order
    ADD CONSTRAINT ck_order_sessions_positive CHECK (sessions IS NULL OR sessions > 0);

ALTER TABLE orders.order_line
    ADD COLUMN IF NOT EXISTS quantity_per_session numeric(14,3) NOT NULL DEFAULT 1;

COMMENT ON COLUMN orders.order_line.quantity_per_session IS
    '31.1 — how much of THIS item is delivered at each attendance. quantity_ordered stays the METERED TOTAL '
    '(sessions x this), so consume, partial approval and the delivering centre''s queue are unchanged. '
    'Defaults to 1, under which a pre-31.1 row''s stored total still equals its session count.';

ALTER TABLE orders.order_line DROP CONSTRAINT IF EXISTS ck_order_line_qty_per_session_positive;  -- migrate-compat: contract-ok (idempotent re-run guard for a constraint this same migration creates immediately below; it has never existed on a previously-deployed build)
ALTER TABLE orders.order_line
    ADD CONSTRAINT ck_order_line_qty_per_session_positive CHECK (quantity_per_session > 0);
