-- orders-service — 0009 BACKFILL: rewrite order_type 'Imaging' → 'Radiology'.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- 29.1 / design 45 §1 — the BACKFILL step, after 0008 taught the CHECK to accept both values.
--
-- UNLIKE THE IDENTITY BACKFILL, THIS ONE REWRITES IN PLACE. A role grant is authority, and authority must be
-- added before it is taken away; an order's type is a LABEL on a row that only this service writes. Leaving
-- both spellings in the table would mean every query that filters by type needs an IN (...) forever, and the
-- one that forgets returns a short worklist rather than an error — a technician's queue that is quietly
-- missing half its orders.
--
-- Safe to rewrite here precisely because 0008 shipped on an earlier deploy: writers still emitting 'Imaging'
-- during the rollout continue to satisfy the CHECK, and their rows are caught by re-running this migration
-- (it is idempotent) or by the switch itself, after which nothing emits the old value.
--
-- The projection consumers were already dual-accepting before this ran — see ProjectionMapping's
-- OrderLinesConsumed arm — so historical events carrying 'Imaging' keep mapping to the same modality. This
-- migration does not and must not touch anything already published.

UPDATE orders.investigation_order
SET order_type = 'Radiology'
WHERE order_type = 'Imaging';

-- provider.contract_service_line's sibling value is backfilled by provider 0012; the two vocabularies are
-- joined by string, so they must not be left disagreeing across a deploy boundary.

DO $$
DECLARE remaining int;
BEGIN
    SELECT count(*) INTO remaining FROM orders.investigation_order WHERE order_type = 'Imaging';
    IF remaining > 0 THEN
        RAISE EXCEPTION '% investigation_order row(s) still typed Imaging', remaining;
    END IF;
END $$;
