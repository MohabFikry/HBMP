-- policy-service — 0013 the query surface (phase 19.5, design 38 §4.4). Additive + idempotent.
--
-- ============================================================================================================
-- 1. THE MEMBER'S BRANCH
-- ============================================================================================================
-- Member query must filter by branch (design 38 §4.4), and until now nothing in the policy schema knew a
-- member's branch. The enrolment REQUEST already carried one — 19.2b passes it to the plan's eligibility rule
-- ("this plan is restricted to these branches") — and then discarded it. So the fact was being asked for,
-- used once, and thrown away.
--
-- This is the ENROLLING branch: where the membership was administered, not where the member is treated. Care
-- happens wherever the member turns up, and encounters already record that in emr; a second, staler answer to
-- "which branch is this person at" is the kind of field that ends up in a report nobody can reconcile.
--
-- Rows written before this migration keep NULL, and a NULL is NOT excluded when a branch-scoped caller runs a
-- member query. Branch scope exists to keep one branch's WORKLIST out of another's; a member search is not a
-- worklist, and hiding every pre-0013 member from the receptionist trying to find them would break the counter
-- to enforce a boundary the row does not even cross. A specific branch FILTER does exclude them, because
-- "members enrolled at Maadi" is a question NULL genuinely does not answer.

ALTER TABLE policy.enrollment ADD COLUMN IF NOT EXISTS branch_id uuid;

-- ============================================================================================================
-- 2. READ PATHS THE TWO QUERIES WALK
-- ============================================================================================================
-- Both queries are multi-criteria over a filtered, sorted, paginated set. These cover the predicates that
-- appear in almost every combination; the rarer ones fall back to a scan of an already-narrowed set.

CREATE INDEX IF NOT EXISTS ix_enrollment_branch
    ON policy.enrollment (branch_id, status) WHERE is_deleted = false AND branch_id IS NOT NULL;

-- member query: the member number is the handle a counter actually has, and it is the default sort.
CREATE INDEX IF NOT EXISTS ix_enrollment_member_no
    ON policy.enrollment (member_no) WHERE is_deleted = false;

-- member query: the beneficiary hop (identifier/name search resolves ids in patient-service first, then lands
-- here), plus the waiting-period predicate.
CREATE INDEX IF NOT EXISTS ix_enrollment_beneficiary_status
    ON policy.enrollment (beneficiary_id, status) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS ix_enrollment_waiting_period
    ON policy.enrollment (waiting_period_ends_on) WHERE is_deleted = false AND waiting_period_ends_on IS NOT NULL;

-- member query: the effective-window filter ("enrolled between these dates") and the relationship facet.
CREATE INDEX IF NOT EXISTS ix_enrollment_effective_window
    ON policy.enrollment (effective_from, effective_to) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS ix_enrollment_relationship
    ON policy.enrollment (relationship) WHERE is_deleted = false;

-- policy query: payer scope is applied as a predicate on EVERY policy read from here on, so it is the one
-- index that must never be missing.
CREATE INDEX IF NOT EXISTS ix_policy_payer_status
    ON policy.policy (payer_id, status) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS ix_policy_effective_window
    ON policy.policy (effective_from, effective_to) WHERE is_deleted = false;

-- policy query: the plan-label filter and the policy→plans hop behind both the label facet and coverage detail.
CREATE INDEX IF NOT EXISTS ix_policy_plan_policy
    ON policy.policy_plan (policy_id, status) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS ix_policy_plan_label
    ON policy.policy_plan (lower(plan_label)) WHERE is_deleted = false;

-- coverage details: the member's own enrolment events, newest first — the effective-dated change history.
CREATE INDEX IF NOT EXISTS ix_enrollment_event_enrollment
    ON policy.enrollment_event (enrollment_id, occurred_at DESC);
