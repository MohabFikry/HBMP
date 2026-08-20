-- approvals-service — 0016: the retrospective review can now be COMPLETED.
--
-- 0002 created the queue: a break-glass decision (emergency approval, director override, manual authorization)
-- sets `retrospective_review_required`, and the partial index below it lists the ones still open.
--
-- Nothing ever closed one. `retrospective_reviewed` appeared in exactly two places in the repository — its own
-- declaration on the entity, and the `NOT retrospective_reviewed` predicate that reads it. No endpoint, service
-- or job ever assigned it. The queue was write-only: every break-glass authorization entered it and none left.
--
-- That matters more than an unfinished feature usually does. The after-the-fact review is the control that
-- makes break-glass acceptable in the first place — an override is defensible BECAUSE somebody checks it
-- afterwards. Unreviewable, the flag records that a review was owed and never that one happened, so the audit
-- trail cannot distinguish "reviewed and upheld" from "nobody ever looked".
--
-- These five columns are what a completed review consists of: who, when, what they concluded, and why. The
-- rationale is NOT NULL-able in practice (the endpoint refuses a blank one with a 422) but is nullable here,
-- because every row that predates this migration has no review and must not be given a fabricated one.
ALTER TABLE approvals.authorization
    ADD COLUMN IF NOT EXISTS retrospective_reviewed_by   text,
    ADD COLUMN IF NOT EXISTS retrospective_reviewed_at   timestamptz,
    ADD COLUMN IF NOT EXISTS retrospective_outcome       text,
    ADD COLUMN IF NOT EXISTS retrospective_rationale     text;

-- Upheld — the break-glass was warranted. NotJustified — it was not.
--
-- NotJustified does NOT reverse the authorization, and the constraint deliberately does not model a third
-- "reversed" state. The care was delivered under it; unwinding it retroactively would refuse a service that
-- has already happened, to a beneficiary who had no part in the decision. It is a FINDING — the thing an
-- oversight report is built from and a conversation with the decider starts from.
-- migrate-compat: contract-ok (the drop is the file's own re-runnability, not a rollout step. The constraint
-- being dropped is the one the very next statement recreates, and it has never existed in any deployed
-- schema — `apply-migrations.sh` replays every file on every pass, so an unconditional ADD CONSTRAINT would
-- fail the second time and stop every service alphabetically after `approvals/`. Nothing outside this file
-- has ever depended on it, so there is no writer to sequence against.)
ALTER TABLE approvals.authorization
    DROP CONSTRAINT IF EXISTS ck_auth_retrospective_outcome;
ALTER TABLE approvals.authorization
    ADD CONSTRAINT ck_auth_retrospective_outcome
    CHECK (retrospective_outcome IS NULL OR retrospective_outcome IN ('Upheld', 'NotJustified'));

-- A reviewed row carries all four fields or none. Half a review — an outcome with no reviewer, a reviewer with
-- no conclusion — is a record that cannot be defended to anyone asking who signed this off.
-- migrate-compat: contract-ok (same as above — drop-then-add is how this file survives its own replay, and
-- the constraint is introduced by this migration, so no deployed writer has ever seen it.)
ALTER TABLE approvals.authorization
    DROP CONSTRAINT IF EXISTS ck_auth_retrospective_complete;
ALTER TABLE approvals.authorization
    ADD CONSTRAINT ck_auth_retrospective_complete
    CHECK (
        NOT retrospective_reviewed
        OR (retrospective_reviewed_by IS NOT NULL
        AND retrospective_reviewed_at IS NOT NULL
        AND retrospective_outcome     IS NOT NULL));

-- The closed half of the queue, read newest-first: "what was decided about last month's overrides" is the
-- other question asked of this table, and 0002's index only answers the open one.
CREATE INDEX IF NOT EXISTS ix_auth_retrospective_closed
    ON approvals.authorization (retrospective_reviewed_at DESC)
    WHERE retrospective_review_required AND retrospective_reviewed;
