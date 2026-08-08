-- eligibility-service — 0003: carry the waiting-period boundary on the coverage projection (phase 24 Gate 3).
--
-- WHY. EligibilityEngine has had a waiting-period branch since 19.2 (the member IS covered, the limits are
-- intact, and the benefit is not yet payable — a hard Ineligible, because no approval can shorten a waiting
-- period). It could never fire in the running system: coverage_projection had nowhere to put the boundary,
-- so EligibilityChecker built its CoverageView without one and the branch was dead code in production. A
-- member inside their waiting period was told Eligible, and the claim that follows is one the policy does
-- not cover. The engine's unit tests passed throughout — they construct the view directly and supply the
-- date the real caller never had.
--
-- policy-service already stores this boundary (WaitingPeriod.EndsOnFor, per benefit category, counted from
-- the enrolment's effective date) precisely so "eligibility, claims and the member's own card cannot
-- disagree about which day cover starts" — design 38 §7.3. This column is the eligibility end of that.
--
-- Expand-only and idempotent: a nullable column with no default. NULL means "no waiting period applies",
-- which is both the correct reading for every row written before this migration and the value the engine
-- already treats as "payable now" — so old rows keep their current behaviour and nothing needs backfilling.
-- The value is refreshed on the next CoverageChanged event for each coverage.

ALTER TABLE eligibility.coverage_projection
    ADD COLUMN IF NOT EXISTS waiting_period_ends_on date;

COMMENT ON COLUMN eligibility.coverage_projection.waiting_period_ends_on IS
    'The LAST day still inside the member''s waiting period for this category, or NULL when none applies. '
    'Sourced from policy-service (WaitingPeriod.EndsOnFor) via CoverageChanged; read by EligibilityEngine.';
