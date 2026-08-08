-- ADR-0035 §5.2 — the third rule family: pre-authorization triggers.
--
-- EXPAND ONLY. The family CHECK widens; nothing existing changes meaning, and a service running the previous
-- build keeps working (it simply never writes 'Preauth').
--
-- ADDITIVE BY CONSTRUCTION. A Preauth rule's action carries a reason and nothing else — there is no field
-- that could mean "stop requiring". The plan version's own RequiresPreauth is a contractual term between the
-- payer and Mersal, and a rule able to switch it off would silently override a contract, surfacing months
-- later as a denied claim nobody could trace to a configuration change. The database cannot enforce that on
-- its own; the SHAPE of the action does, which is why it is a record with one string.

ALTER TABLE approvals.rule DROP CONSTRAINT IF EXISTS ck_rule_family;  -- migrate-compat: contract-ok (widening a CHECK in place; the old set is a strict subset of the new one, so no row can fail and no deploy order matters)
ALTER TABLE approvals.rule ADD CONSTRAINT ck_rule_family
    CHECK (family IN ('Routing', 'Sla', 'Preauth'));
