-- policy-service — 0018 batch-level coverage defaults on a bulk job.
--
-- An intake batch is normally one cohort on one plan and one network tier, differing member by member only in
-- the contribution. Requiring the same two values in every one of five hundred rows is five hundred chances to
-- mistype one, and the mistype is invisible: the row is perfectly valid, it just enrols somebody onto the
-- wrong plan.
--
-- So the operator states them ONCE at upload and they fill any cell the file leaves blank. A row that names
-- its own value keeps it — the default fills a gap, it never overrides a stated fact.
--
-- Recorded on the JOB rather than passed per request, because validate and commit are separate calls, often
-- minutes apart, and a default that applied at validate but not at commit would make the dry run a preview of
-- something else. Contribution is deliberately absent: it is the value that varies per member, and a single
-- batch-wide figure is exactly the mistake this table should not make easy.

ALTER TABLE policy.bulk_job ADD COLUMN IF NOT EXISTS default_plan_id         uuid;
ALTER TABLE policy.bulk_job ADD COLUMN IF NOT EXISTS default_network_tier_id uuid;
ALTER TABLE policy.bulk_job ADD COLUMN IF NOT EXISTS default_branch_id       uuid;
