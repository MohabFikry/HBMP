-- eligibility-service — 0004: carry the PLAN VERSION on the coverage projection (19.2b).
--
-- WHY. libs/benefit-pricing is described in its own header as "the ONE path from (provider, service date,
-- benefit category) to what the member pays", so that "the amount a receptionist reads off an eligibility
-- card and the amount a claim finally charges" cannot differ. Every entry point to it needs a plan version,
-- and no consumer could name one: POST /check accepts planVersionId from the CALLER and skips pricing when
-- it is absent, and claims-service ships UnresolvedPlanVersionForClaim, which returns null by design "until
-- 19.2b". So the shared path existed and nothing could reach it.
--
-- The link itself was never the missing part. policy.coverage.source_plan_version_id is populated on all 96
-- rows — the version each coverage was written under — it simply was not published or projected. This column
-- is the eligibility end of that, fed from CoverageChanged exactly as waiting_period_ends_on is (0003).
--
-- WHY THE STORED VERSION AND NOT "THE CURRENT ACTIVE ONE". Pricing against whichever version happens to be
-- active today is the precise bug the effective-dated plan layer exists to prevent: a version activated in
-- March would silently reprice February's care. UnresolvedPlanVersionForClaim's own comment says so. NULL
-- here therefore keeps meaning "do not price", never "use the newest".
--
-- Expand-only and idempotent: a nullable column, no default. NULL is correct for every row written before
-- this migration — the caller-supplied path is unchanged and cost-share simply stays indeterminate, which is
-- already rendered as "cannot be quoted" rather than as zero. Rows refresh on the next CoverageChanged.

ALTER TABLE eligibility.coverage_projection
    ADD COLUMN IF NOT EXISTS plan_version_id uuid;

COMMENT ON COLUMN eligibility.coverage_projection.plan_version_id IS
    'The plan version this coverage was written under (policy.coverage.source_plan_version_id), via '
    'CoverageChanged. Used to price cost share through libs/benefit-pricing. NULL means the version is '
    'unknown and cost share must be reported as indeterminate — never priced at zero.';
