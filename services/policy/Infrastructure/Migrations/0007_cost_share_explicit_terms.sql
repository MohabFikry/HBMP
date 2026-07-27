-- policy-service — 0007 two cost-share terms that were silent defaults (phase 19.1b refinement). Additive.
--
-- Both of these change what a real person pays, and until now the platform decided each one implicitly:
--
--   deductible_waived              Whether the plan's deductible applies to THIS benefit category. Primary care
--                                  commonly waives it. This is deliberately NOT modelled as "set the deductible
--                                  to zero on the rule": "this category is exempt" and "this plan has no
--                                  deductible" are different statements that survive a plan amendment
--                                  differently — the exemption should follow the category, a zero should not.
--
--   copay_counts_toward_deductible Whether the co-pay the member pays at this tier accrues toward their
--                                  deductible for later services. It does not change what they pay today; it
--                                  changes what they pay NEXT, which is why leaving it implicit is worse than
--                                  getting it wrong loudly. The running accumulator that consumes it arrives
--                                  with member-level accumulators (19.2); the field exists now so the value is
--                                  captured from day one rather than back-filled from an assumption later.
--
-- Both default to the behaviour the calculator had before this migration, so no existing draft changes meaning.
--
-- OPEN PRODUCT QUESTION (ADR-0019, unconfirmed): whether Mersal charges cost-share to refugee beneficiaries at
-- all. For self-funded charity policies the answer may be no deductible, no coinsurance, at most a nominal
-- co-pay — in which case this grid is live only for donor- and government-funded payers. The schema supports
-- either answer; the decision is the Medical Director's and Finance's, not this migration's.

ALTER TABLE policy.benefit_rule
    ADD COLUMN IF NOT EXISTS deductible_waived boolean NOT NULL DEFAULT false;

ALTER TABLE policy.benefit_rule_tier
    ADD COLUMN IF NOT EXISTS copay_counts_toward_deductible boolean NOT NULL DEFAULT false;

-- A waiver on a category that has no deductible to waive is dead configuration: it reads as a deliberate
-- concession in every UI that renders it while conceding nothing.
ALTER TABLE policy.benefit_rule
    DROP CONSTRAINT IF EXISTS ck_benefit_rule_waiver_needs_deductible;  -- migrate-compat: contract-ok (idempotent drop-then-readd of a constraint this migration itself introduces; nothing pre-existing is relaxed)
ALTER TABLE policy.benefit_rule
    ADD CONSTRAINT ck_benefit_rule_waiver_needs_deductible
    CHECK (NOT deductible_waived OR deductible IS NOT NULL);

-- Likewise a co-pay that "counts toward the deductible" when no co-pay is configured at this tier.
ALTER TABLE policy.benefit_rule_tier
    DROP CONSTRAINT IF EXISTS ck_brt_accrual_needs_copay;  -- migrate-compat: contract-ok (idempotent drop-then-readd of a constraint this migration itself introduces)
ALTER TABLE policy.benefit_rule_tier
    ADD CONSTRAINT ck_brt_accrual_needs_copay
    CHECK (NOT copay_counts_toward_deductible OR copay_fixed IS NOT NULL OR copay_percent IS NOT NULL);
