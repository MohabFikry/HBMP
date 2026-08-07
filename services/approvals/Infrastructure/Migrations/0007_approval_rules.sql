-- ADR-0035 §5.1/§5.4 — the approvals engine's rule store, and the queue a request routes to.
--
-- EXPAND ONLY. One new table and one nullable column; nothing existing changes shape or meaning, and a
-- service running the previous build keeps working against this schema unchanged.
--
-- Routing and SLA are the first families deliberately: they change WHO decides and BY WHEN, never WHAT is
-- decided. Nothing this table can express approves or refuses anything.

-- ---- the rules ------------------------------------------------------------------------------------------
--
-- Effective-dated and append-only, the same governance shape as master data and for the same reason: a
-- request routed last Tuesday must be explainable against the rules in force last Tuesday, not today's. An
-- UPDATE of a live rule would rewrite the basis of every past routing decision with nothing to point at.
CREATE TABLE IF NOT EXISTS approvals.rule (
    rule_id        uuid PRIMARY KEY,
    tenant_id      text NOT NULL,
    family         text NOT NULL,
    -- Lower runs first. Ties break on rule_id in the evaluator, so the order is TOTAL — without that, which
    -- of two same-priority rules wins would depend on the order the database happened to return rows.
    priority       integer NOT NULL,
    predicate_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    action_json    jsonb NOT NULL DEFAULT '{}'::jsonb,
    effective_from timestamptz NOT NULL,
    effective_to   timestamptz NULL,
    version_no     integer NOT NULL DEFAULT 1,
    enabled        boolean NOT NULL DEFAULT true,
    -- Who and why. Mandatory: a rule that silently redirected a queue for three weeks, with no account of who
    -- decided that or what they were solving, is not something anybody can review afterwards.
    authored_by    text NOT NULL,
    rationale      text NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now()
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_rule_family') THEN
        ALTER TABLE approvals.rule ADD CONSTRAINT ck_rule_family
            CHECK (family IN ('Routing', 'Sla'));
    END IF;

    -- A blank rationale is the same as none, and the column being NOT NULL would not catch ''.
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_rule_rationale_not_blank') THEN
        ALTER TABLE approvals.rule ADD CONSTRAINT ck_rule_rationale_not_blank
            CHECK (length(btrim(rationale)) > 0);
    END IF;

    -- A window that closes before it opens is a rule that can never fire, and it would sit in the list
    -- looking live.
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_rule_window') THEN
        ALTER TABLE approvals.rule ADD CONSTRAINT ck_rule_window
            CHECK (effective_to IS NULL OR effective_to > effective_from);
    END IF;
END $$;

-- The evaluator reads "rules of this family in force now", so the index carries the family and the window.
CREATE INDEX IF NOT EXISTS ix_rule_in_force
    ON approvals.rule (tenant_id, family, priority, rule_id)
    WHERE enabled = true AND effective_to IS NULL;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE ON approvals.rule TO hbmp_app;
    END IF;
END $$;

ALTER TABLE approvals.rule ENABLE ROW LEVEL SECURITY;
ALTER TABLE approvals.rule FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_rule ON approvals.rule;
CREATE POLICY rls_rule ON approvals.rule
    USING (tenant_id = current_setting('app.tenant_id', true));

-- ---- where a request was routed -------------------------------------------------------------------------
--
-- Nullable, because every row written before rules existed was never routed by one, and a default of
-- 'default' would claim they were. NULL here means "no rule has looked at this yet", which is a different
-- fact from "a rule sent this to the default queue" — and the second is a decision worth being able to see.
ALTER TABLE approvals.authorization
    ADD COLUMN IF NOT EXISTS routed_queue text NULL,
    -- Which rule did it, so a routing decision can be explained without re-deriving it. The rule may have
    -- been superseded since; the id still resolves against the append-only history.
    ADD COLUMN IF NOT EXISTS routed_by_rule uuid NULL;

CREATE INDEX IF NOT EXISTS ix_authorization_routed_queue
    ON approvals.authorization (tenant_id, routed_queue)
    WHERE routed_queue IS NOT NULL;
