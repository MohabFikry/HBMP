-- policy-service — 0008 PAS layer part 2: policy_plan, member_group, enrollment, enrollment_event
-- (phase 19.2 + 19.2b, design 38 §3–§4.2). Additive + idempotent.
--
-- 19.2 AND 19.2b SHIP IN ONE WAVE, deliberately. 19.2b changes 19.2's shape: a policy no longer points at one
-- plan version — it offers 1..n plans via policy_plan, and a member is elected onto exactly one. Landing 19.2
-- first would create enrollments with no plan, and then either a nullable FK we could never make NOT NULL, or
-- a backfill inventing which plan each member "must have meant".
--
-- ============================================================================================================
-- THE RANGE OPERATOR HERE IS INCLUSIVE, AND THAT IS NOT AN OVERSIGHT.
-- ============================================================================================================
-- plan_version (0005) uses a HALF-OPEN window [from, to): a successor starts on exactly the day its
-- predecessor ends, because a benefit configuration boundary is naturally "the first day the new rules apply".
--
-- enrollment and coverage use an INCLUSIVE window [from, to]: a termination effective 31 December means the
-- member IS covered on 31 December, because a membership boundary is naturally "the last day of cover". That
-- is also what `EligibilityEngine` already implements for coverage (`EffectiveTo >= onDate`) — a shipped
-- adjudication path. Making enrollment half-open would either contradict the engine or require changing it,
-- silently moving every member's last covered day by one.
--
-- Both conventions are correct in their own domain. Mixing them WITHOUT saying so is how an off-by-one becomes
-- a person turned away at a counter, so each range below states which it is.
-- ============================================================================================================

CREATE EXTENSION IF NOT EXISTS btree_gist;

-- ---- policy: payer, renewal chain, capacity (19.2) ---------------------------------------------------------
-- The policy does NOT gain a plan_version_id. Plans hang off it via policy_plan (19.2b).
ALTER TABLE policy.policy ADD COLUMN IF NOT EXISTS payer_id uuid REFERENCES policy.payer(payer_id);
ALTER TABLE policy.policy ADD COLUMN IF NOT EXISTS previous_policy_id uuid REFERENCES policy.policy(policy_id);
ALTER TABLE policy.policy ADD COLUMN IF NOT EXISTS max_members int CHECK (max_members IS NULL OR max_members > 0);
CREATE INDEX IF NOT EXISTS ix_policy_payer ON policy.policy (payer_id);

-- ---- policy_plan: the plans a policy offers (19.2b) --------------------------------------------------------
CREATE TABLE IF NOT EXISTS policy.policy_plan (
    policy_plan_id   uuid PRIMARY KEY,
    tenant_id        text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',
    policy_id        uuid NOT NULL REFERENCES policy.policy(policy_id),
    plan_version_id  uuid NOT NULL REFERENCES policy.plan_version(plan_version_id),
    plan_label       varchar(80) NOT NULL,          -- 'Standard' | 'Oncology' | 'Staff' | 'Dependents'
    effective_from   date NOT NULL,
    effective_to     date,                          -- INCLUSIVE last day; NULL = open-ended
    is_default       boolean NOT NULL DEFAULT false,

    -- DECLARATIVE criteria evaluated by the rules engine, never hard-coded:
    --   {"groupIds":[...], "relationships":["Principal"], "minAge":18, "maxAge":64, "branchIds":[...]}
    -- Anything a plan restricts election on lives here so the restriction is data an administrator can read
    -- and change, not a branch in code they cannot see.
    eligibility_rule jsonb,

    max_members      int CHECK (max_members IS NULL OR max_members > 0),
    status           varchar(12) NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Closed')),
    is_deleted       boolean NOT NULL DEFAULT false,
    row_version      int NOT NULL DEFAULT 0,
    created_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid,
    updated_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,
    CONSTRAINT ck_policy_plan_dates CHECK (effective_to IS NULL OR effective_to >= effective_from)
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_policy_plan_label
    ON policy.policy_plan (policy_id, plan_label) WHERE NOT is_deleted;
-- At most one default per policy. Enrolment with no plan named resolves to the default, so two defaults would
-- make that resolution a coin toss — for a choice that decides what the member is entitled to.
CREATE UNIQUE INDEX IF NOT EXISTS uq_policy_plan_single_default
    ON policy.policy_plan (policy_id) WHERE is_default AND status = 'Active' AND NOT is_deleted;
-- The same plan version must not be offered twice concurrently under one policy: two labels pointing at one
-- version for overlapping windows is either a mistake or two names for one thing, and both mislead a report.
-- INCLUSIVE range: two windows that abut on the same day DO overlap here (unlike plan_version).
ALTER TABLE policy.policy_plan DROP CONSTRAINT IF EXISTS ex_policy_plan_no_overlap;  -- migrate-compat: contract-ok (idempotent drop-then-readd of a constraint this migration itself introduces)
ALTER TABLE policy.policy_plan ADD CONSTRAINT ex_policy_plan_no_overlap EXCLUDE USING gist (
    tenant_id WITH =,
    policy_id WITH =,
    plan_version_id WITH =,
    daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]') WITH &&
) WHERE (status = 'Active' AND NOT is_deleted);
CREATE INDEX IF NOT EXISTS ix_policy_plan_policy ON policy.policy_plan (policy_id);

-- ---- member_group: cohorts inside a policy (19.2) ----------------------------------------------------------
CREATE TABLE IF NOT EXISTS policy.member_group (
    group_id       uuid PRIMARY KEY,
    tenant_id      text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',
    policy_id      uuid NOT NULL REFERENCES policy.policy(policy_id),
    group_code     varchar(40) NOT NULL,
    name_en        text NOT NULL,
    name_ar        text NOT NULL,
    group_type     varchar(20) NOT NULL CHECK (group_type IN ('Programme','Cohort','BranchCaseload','Campaign')),
    effective_from date NOT NULL,
    effective_to   date,                            -- INCLUSIVE
    status         varchar(12) NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Closed')),
    is_deleted     boolean NOT NULL DEFAULT false,
    row_version    int NOT NULL DEFAULT 0,
    created_at     timestamptz NOT NULL DEFAULT now(),
    created_by     uuid,
    updated_at     timestamptz NOT NULL DEFAULT now(),
    updated_by     uuid,
    CONSTRAINT ck_member_group_dates CHECK (effective_to IS NULL OR effective_to >= effective_from)
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_member_group_code
    ON policy.member_group (policy_id, group_code) WHERE NOT is_deleted;

-- ---- enrollment: the membership record (19.2 + 19.2b) ------------------------------------------------------
CREATE TABLE IF NOT EXISTS policy.enrollment (
    enrollment_id           uuid PRIMARY KEY,
    tenant_id               text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',
    beneficiary_id          uuid NOT NULL,                       -- logical FK to patient-service (a VALUE)
    policy_id               uuid NOT NULL REFERENCES policy.policy(policy_id),
    -- 19.2b: NOT NULL. A member is always elected onto exactly one plan; there is no "enrolled but on nothing".
    policy_plan_id          uuid NOT NULL REFERENCES policy.policy_plan(policy_plan_id),
    group_id                uuid REFERENCES policy.member_group(group_id),
    member_no               varchar(30) NOT NULL,
    relationship            varchar(12) NOT NULL CHECK (relationship IN ('Principal','Spouse','Child','Dependent')),
    principal_enrollment_id uuid REFERENCES policy.enrollment(enrollment_id),
    effective_from          date NOT NULL,
    effective_to            date,                                -- INCLUSIVE last day of cover; NULL = open
    waiting_period_ends_on  date,                                -- last day INSIDE the waiting period
    status                  varchar(12) NOT NULL DEFAULT 'Pending'
                              CHECK (status IN ('Pending','Active','Suspended','Terminated','Cancelled')),
    termination_reason      text,
    source_plan_version_id  uuid REFERENCES policy.plan_version(plan_version_id),   -- provenance
    -- Replay guard. The overlap exclusion below already makes a DOUBLE enrolment structurally impossible, but
    -- it answers a retry with a 23P01 rather than the row the caller already created. This turns a repeated
    -- request into the same answer, which is what an Idempotency-Key promises.
    idempotency_key         varchar(120),
    is_deleted              boolean NOT NULL DEFAULT false,
    row_version             int NOT NULL DEFAULT 0,
    created_at              timestamptz NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz NOT NULL DEFAULT now(),
    updated_by              uuid,
    CONSTRAINT ck_enrollment_dates CHECK (effective_to IS NULL OR effective_to >= effective_from),
    -- Terminating without saying why makes the member's history unreadable to whoever picks it up next, and a
    -- termination is the change most likely to be disputed.
    CONSTRAINT ck_enrollment_termination_reason CHECK (status <> 'Terminated' OR termination_reason IS NOT NULL),
    -- A dependent hangs off a principal; a principal does not hang off anything.
    CONSTRAINT ck_enrollment_principal_link CHECK (
        (relationship = 'Principal' AND principal_enrollment_id IS NULL)
        OR (relationship <> 'Principal' AND principal_enrollment_id IS NOT NULL)
    )
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_enrollment_member_no
    ON policy.enrollment (tenant_id, member_no) WHERE NOT is_deleted;
CREATE UNIQUE INDEX IF NOT EXISTS uq_enrollment_idempotency
    ON policy.enrollment (tenant_id, idempotency_key) WHERE idempotency_key IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_enrollment_beneficiary ON policy.enrollment (beneficiary_id);
CREATE INDEX IF NOT EXISTS ix_enrollment_policy ON policy.enrollment (policy_id, status);
CREATE INDEX IF NOT EXISTS ix_enrollment_group ON policy.enrollment (group_id);
CREATE INDEX IF NOT EXISTS ix_enrollment_plan ON policy.enrollment (policy_plan_id);

-- THE overlap invariant. One beneficiary cannot hold two live memberships of the same policy over the same
-- days: coverage would be generated twice, two accumulators would exist for one entitlement, and which one a
-- consume decremented would depend on query order. Suspended counts as live — a suspension pauses the benefit,
-- it does not vacate the membership. INCLUSIVE range, matching the window semantics above.
ALTER TABLE policy.enrollment DROP CONSTRAINT IF EXISTS ex_enrollment_no_overlap;  -- migrate-compat: contract-ok (idempotent drop-then-readd of a constraint this migration itself introduces)
ALTER TABLE policy.enrollment ADD CONSTRAINT ex_enrollment_no_overlap EXCLUDE USING gist (
    tenant_id WITH =,
    beneficiary_id WITH =,
    policy_id WITH =,
    daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]') WITH &&
) WHERE (status IN ('Active','Suspended') AND NOT is_deleted);

-- ---- enrollment_event: append-only history (19.2) ----------------------------------------------------------
-- Retro-effective changes are EVENTS, never edits (design 38 §7.6). A termination back-dated to last month has
-- to leave a trace of when it was actually decided and by whom, or the record cannot be defended.
CREATE TABLE IF NOT EXISTS policy.enrollment_event (
    event_id       uuid PRIMARY KEY,
    tenant_id      text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',
    enrollment_id  uuid NOT NULL REFERENCES policy.enrollment(enrollment_id),
    event_type     varchar(20) NOT NULL CHECK (event_type IN
                     ('Enrolled','GroupChanged','PlanChanged','Suspended','Reinstated','Terminated','Corrected')),
    effective_date date NOT NULL,
    reason         text,
    payload        jsonb NOT NULL DEFAULT '{}'::jsonb,
    actor_user_id  uuid,
    occurred_at    timestamptz NOT NULL DEFAULT now(),
    -- Every change that removes or redirects entitlement states why. Enrolment and reinstatement may not.
    CONSTRAINT ck_enrollment_event_reason CHECK (
        event_type NOT IN ('Terminated','PlanChanged','Corrected') OR reason IS NOT NULL
    )
);
CREATE INDEX IF NOT EXISTS ix_enrollment_event_enrollment
    ON policy.enrollment_event (enrollment_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_enrollment_event_type ON policy.enrollment_event (event_type);

-- Append-only, enforced. An event log that can be edited is not a log — and this one is the only account of
-- why a member's entitlement changed.
CREATE OR REPLACE FUNCTION policy.guard_enrollment_event_append_only()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'enrollment_event is append-only: correct the record with a new event, never by editing one'
        USING ERRCODE = 'raise_exception';
END $$;
DROP TRIGGER IF EXISTS trg_enrollment_event_append_only ON policy.enrollment_event;
CREATE TRIGGER trg_enrollment_event_append_only BEFORE UPDATE OR DELETE ON policy.enrollment_event
    FOR EACH ROW EXECUTE FUNCTION policy.guard_enrollment_event_append_only();

-- ---- coverage provenance (19.2) ----------------------------------------------------------------------------
-- A member's coverage is GENERATED from a plan version at enrolment. Recording which version and which
-- enrolment produced it is what makes an entitlement explainable — "why am I covered for this, and for how
-- much" is answerable by following these two columns back to a dated, immutable configuration.
ALTER TABLE policy.coverage ADD COLUMN IF NOT EXISTS source_plan_version_id uuid REFERENCES policy.plan_version(plan_version_id);
ALTER TABLE policy.coverage ADD COLUMN IF NOT EXISTS enrollment_id uuid REFERENCES policy.enrollment(enrollment_id);
CREATE INDEX IF NOT EXISTS ix_coverage_enrollment ON policy.coverage (enrollment_id);

-- ---- Grants + tenant RLS (ADR-0011, same shape as 0005) ----------------------------------------------------
GRANT SELECT, INSERT, UPDATE, DELETE
    ON policy.policy_plan, policy.member_group, policy.enrollment TO hbmp_app;
-- enrollment_event: no UPDATE, no DELETE. The trigger above refuses them anyway; withholding the grant means
-- a bug cannot even attempt it.
GRANT SELECT, INSERT ON policy.enrollment_event TO hbmp_app;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['policy_plan','member_group','enrollment','enrollment_event']
    LOOP
        EXECUTE format('ALTER TABLE policy.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE policy.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON policy.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON policy.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;
