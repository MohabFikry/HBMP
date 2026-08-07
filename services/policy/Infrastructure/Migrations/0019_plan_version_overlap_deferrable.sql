-- policy-service — 0019 the plan-version overlap constraint is checked AT COMMIT, not per row.
--
-- ============================================================================================================
-- THE BUG
-- ============================================================================================================
-- Activating a plan version is a two-row swap inside one transaction: the outgoing version is closed at the
-- incoming one's start date and marked Superseded, and the incoming one becomes Active. Half-open ranges, so
-- the two abut exactly — [.., from) then [from, ..) — and the end state never overlaps.
--
-- `ex_plan_version_no_overlap` is checked PER ROW. So the end state is irrelevant: if the incoming version is
-- updated before the outgoing one is closed, then for the duration of one statement there are two non-Draft
-- rows both running to infinity, and the constraint fires. Which row EF updates first is not something the
-- handler decides, so the same amendment succeeded on one plan and failed on the next with
--
--     409 OVERLAPPING_VERSION — "Another version of this plan already covers part of that effective range."
--
-- which is a true sentence about a state that existed for microseconds and was never committed. An operator
-- reading it goes looking for a conflicting version that does not exist.
--
-- ============================================================================================================
-- THE FIX
-- ============================================================================================================
-- DEFERRABLE INITIALLY DEFERRED: the exclusion is evaluated once, at COMMIT, against the state that is
-- actually being committed. This is the standard treatment for a swap under an exclusion constraint, and it
-- weakens nothing — a transaction that genuinely leaves two overlapping versions still fails, and still fails
-- before anything is durable. What it stops is a correct transaction failing on the order its statements
-- happened to be emitted in.
--
-- 0005 created the constraint inline on the table; recreating it is the only way to change its timing.
-- Recreated with the SAME predicate and the same WHERE clause, so nothing about which rows it governs moves.

ALTER TABLE policy.plan_version DROP CONSTRAINT IF EXISTS ex_plan_version_no_overlap;  -- migrate-compat: contract-ok (recreated immediately below with an identical predicate; only the check TIMING changes, so no state the old constraint rejected becomes acceptable)

ALTER TABLE policy.plan_version
    ADD CONSTRAINT ex_plan_version_no_overlap EXCLUDE USING gist (
        tenant_id WITH =,
        plan_id WITH =,
        daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[)') WITH &&
    ) WHERE (status <> 'Draft')
    DEFERRABLE INITIALLY DEFERRED;

COMMENT ON CONSTRAINT ex_plan_version_no_overlap ON policy.plan_version IS
    'No two non-Draft versions of one plan may cover the same day. DEFERRED because activation is a two-row '
    'swap: the intermediate state where the outgoing version is not yet closed is not a state anyone commits.';
