-- approvals-service — 0010: an authorization must be ATTRIBUTABLE, which is not the same as provider-raised.
--
-- ============================================================================================================
-- THE CONSTRAINT THIS REPLACES ENCODED AN ASSUMPTION THAT WAS NEVER EXERCISED
-- ============================================================================================================
--
-- Phase 7 wrote `CHECK (source = 'Manual' OR requesting_provider_id IS NOT NULL)` with the comment "manual
-- authorizations have no requesting provider; all others must name one". It held for four years of commits
-- because every path that actually created a row was raised BY a provider-scoped account:
--
--   ValidityExtension  a pharmacist or technician asking for an expired item — endpoint 422s without a provider
--   OrderLine (subst.) a technician proposing an alternative examination — same 422
--   Fulfilment         exempted in 0006, because a dispense is a record and not a request
--
-- The two sources the constraint was WRITTEN for — a gated investigation order and a gated prescription —
-- never created a row at all. `POST /api/v1/authorizations` was built for "the OrderPendingApproval|RxSubmitted
-- event consumer" and no such consumer existed, so nothing ever tested the rule against the case it names.
--
-- It does not hold there, and it cannot be made to. A doctor's token is PRACTITIONER-scoped and carries no
-- `provider_id` — which is why `pharmacy.prescription` has no such column and why an order raised in a Mersal
-- branch carries `ordering_branch_id` instead. Requiring a provider on those paths would mean dead-lettering
-- every gated prescription in the platform to satisfy a field that has no value to put in it.
--
-- ============================================================================================================
-- WHAT THE RULE ACTUALLY IS
-- ============================================================================================================
--
-- An authorization must be attributable to SOMEBODY: a provider that raised it, or a person who did. That is
-- the property the original was reaching for, and it is true of every path, including the two new ones —
-- `created_by` carries the ordering clinician, which is also who the decision notice is addressed to.
--
-- This is a WIDENING, so it is safe in both directions: every row that satisfied the old constraint satisfies
-- this one, and a previous-build instance writing under the old rule cannot produce a row this rejects. The
-- endpoint's own 422s are unchanged — an external caller posting a non-manual request still has to name a
-- provider, because there the missing provider means "this system cannot say who is asking".
--
-- Idempotent (expand/contract): drop-then-add under the same name.

ALTER TABLE approvals.authorization DROP CONSTRAINT IF EXISTS authorization_check;  -- migrate-compat: contract-ok (WIDENS the constraint; the replacement is added in the same migration, three statements below, and admits every row the old one did — so no value that was legal before becomes illegal, and a previous-build instance writing under the old rule cannot produce a row this rejects)

ALTER TABLE approvals.authorization
    ADD CONSTRAINT authorization_check
    CHECK (kind = 'Fulfilment'
           OR source = 'Manual'
           OR requesting_provider_id IS NOT NULL
           OR created_by IS NOT NULL);

COMMENT ON COLUMN approvals.authorization.requesting_provider_id IS
    'The provider that RAISED the request, where one did: a pharmacy asking for a validity extension, a lab '
    'proposing a substitution. NULL on a request routed from a clinician — a practitioner-scoped token '
    'carries no provider, and created_by names the person instead. The CHECK requires one or the other.';
