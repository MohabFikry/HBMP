-- DEV BACKFILL — put the PLAN on eligibility's coverage projection.
--
-- eligibility migration 0005 added `plan_id`; policy-service now publishes it on CoverageChanged, so every
-- coverage written from here on carries it. Rows projected BEFORE that do not, and a coverage with no plan
-- falls back to the version it was enrolled under — which is the behaviour 0005 exists to replace.
--
-- WHY THIS IS A dev SCRIPT AND NOT A MIGRATION. It reads `policy.coverage`, and a migration belongs to one
-- schema: eligibility must not learn to query policy's tables, or the projection stops being a projection.
-- In a real environment the equivalent is a coverage re-publish from policy, which walks the same rows
-- through the event seam and leaves an audit trail. This is the dev shortcut for a dev database.
--
-- Idempotent. Rows whose plan cannot be resolved are LEFT NULL rather than guessed: a wrong plan prices a
-- member against somebody else's benefit, and "unknown" already has correct behaviour behind it.

UPDATE eligibility.coverage_projection ecp
SET plan_id = pv.plan_id
FROM policy.coverage c
JOIN policy.plan_version pv ON pv.plan_version_id = c.source_plan_version_id
WHERE ecp.coverage_id = c.coverage_id
  AND ecp.plan_id IS NULL
  AND c.source_plan_version_id IS NOT NULL;

SELECT count(*) FILTER (WHERE plan_id IS NOT NULL) AS with_plan,
       count(*) FILTER (WHERE plan_id IS NULL)     AS without_plan
FROM eligibility.coverage_projection;
