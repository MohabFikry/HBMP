-- orders-service — 0008 EXPAND: order_type accepts 'Radiology' as well as 'Imaging'. Additive.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- 29.1 / design 45 §1 — the EXPAND step. 0009 backfills the rows; the CHECK stops accepting 'Imaging' only in
-- Migrations/deferred/0010_radiology_order_type_contract.sql, which does not ship on this deploy.
--
-- EXPAND BEFORE BACKFILL, ALWAYS IN THAT ORDER. A CHECK that admits both values must be committed and
-- deployed before a single row is rewritten, because the writers are still emitting 'Imaging' while the
-- backfill runs. Reversing the two — or collapsing them into one migration — means every in-flight insert
-- between the UPDATE and the redeploy hits a constraint violation, and orders-service's failure mode there is
-- a 500 on order placement.
--
-- The order_type CHECK is an unnamed table constraint from 0001, so it is dropped by its generated name and
-- re-added under an explicit one. Naming it now means the contract step does not have to guess.
--
-- ON THE `migrate-compat: contract-ok` ACKNOWLEDGEMENTS BELOW. Those two DROPs are what the paragraph above
-- describes: the column is never left unconstrained, and the CHECK that replaces them is a strict SUPERSET —
-- every value the old one accepted, the new one accepts. A dropped-and-widened CHECK cannot break a writer
-- that is still emitting the old value, which is the compatibility the gate is protecting. The narrowing is
-- the deferred contract migration named above, and it is not in this deploy.

ALTER TABLE orders.investigation_order DROP CONSTRAINT IF EXISTS investigation_order_order_type_check;  -- migrate-compat: contract-ok (widen-only; see header)
ALTER TABLE orders.investigation_order DROP CONSTRAINT IF EXISTS ck_investigation_order_order_type;  -- migrate-compat: contract-ok (widen-only; see header)
ALTER TABLE orders.investigation_order
    ADD CONSTRAINT ck_investigation_order_order_type
    CHECK (order_type IN ('Lab','Imaging','Radiology','Procedure'));
