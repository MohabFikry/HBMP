-- eligibility-service — 0005 the PLAN a coverage belongs to, alongside the version it was written under.
-- Additive, nullable, backward-compatible.
--
-- ============================================================================================================
-- WHY BOTH, AND WHY THE VERSION ALONE WAS WRONG
-- ============================================================================================================
-- 0004 added `plan_version_id` so the shared cost-share path had something to price against, and resolved it
-- from the version the member's coverage was PROJECTED FROM. That pinned every future quote to the terms in
-- force on the day they enrolled: amend the plan in February and nobody already on it ever sees the change,
-- because their coverage still names January's version.
--
-- The rule the effective-dated plan layer actually encodes — and which `CoverageDetailEndpoints` already
-- applies — is: resolve the version IN FORCE ON THE SERVICE DATE, for the member's plan. February's care
-- prices at February's version; today's care prices at today's. One rule, both cases right. That needs the
-- PLAN, because the version is only reachable through it.
--
-- `plan_version_id` stays and keeps its meaning: provenance, and the fallback when the plan is unknown or
-- policy-service cannot be reached. A quote that can resolve neither is still reported as indeterminate
-- rather than priced at zero — a zero at a counter reads as "free".

ALTER TABLE eligibility.coverage_projection
    ADD COLUMN IF NOT EXISTS plan_id uuid;

COMMENT ON COLUMN eligibility.coverage_projection.plan_id IS
    'The benefit plan this coverage belongs to. Used to resolve the plan version IN FORCE on the service '
    'date; plan_version_id is the provenance and the fallback. NULL for a coverage created outside the '
    'enrolment path, which is honest rather than guessed.';
