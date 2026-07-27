-- policy-service — 0005 PAS layer part 1: payer, plan, effective-dated immutable plan_version + benefit_rule
-- (phase 19.1, design 38-policy-member-administration.md §3). Additive + idempotent (expand/contract).
--
-- WHY THIS EXISTS. Until now a policy carried a free-text `sponsor` and its benefits hung directly off the
-- policy instance, so every policy was bespoke and nothing was versioned. The single most important property
-- of a Policy Administration System is that a claim/authorization is adjudicated against **the benefit rules
-- in force on the SERVICE DATE**, not the rules that happen to be current when the adjudicator looks. That
-- requires benefit configuration to live on an effective-dated, immutable version — which is what
-- plan_version + benefit_rule are.
--
-- THE RANGE OPERATOR IS HALF-OPEN. Design 38 §7.1 defines the resolution as
-- `service_date ∈ [effective_from, effective_to)` — effective_to is EXCLUSIVE. A successor version therefore
-- starts on exactly the day its predecessor ends, with no gap and no double-cover. Every range below uses
-- '[)' for that reason; note this differs from provider.provider_contract, which uses '[]'.
--
-- OVERLAP EXCLUSION IS WIDER THAN "ACTIVE". The build prompt asks for no overlapping *Active* ranges. That
-- is necessary but not sufficient: the resolver must also resolve a PAST service date unambiguously, and a
-- past date lands on a Superseded (or Retired) version. If two superseded versions could overlap, the
-- resolver would have two right answers. The constraint therefore covers every resolvable version
-- (status <> 'Draft'). Drafts are excluded because a draft has never been in force and is freely editable.

CREATE EXTENSION IF NOT EXISTS btree_gist;   -- uuid/text equality inside a GiST exclusion constraint

-- ---- payer / sponsor -------------------------------------------------------------------------------------
-- Replaces policy.sponsor (free text) as a first-class entity so every query, report and settlement grouping
-- can be scoped and secured to a payer (design 38 §3, §6 "payer scope").
CREATE TABLE IF NOT EXISTS policy.payer (
    payer_id    uuid PRIMARY KEY,
    tenant_id   text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',
    payer_code  varchar(30) NOT NULL,
    name_en     text NOT NULL,
    name_ar     text NOT NULL,
    payer_type  varchar(16) NOT NULL CHECK (payer_type IN ('SelfFunded','Donor','Government','PartnerNGO','Insurer')),
    contact     jsonb NOT NULL DEFAULT '{}'::jsonb,
    status      varchar(16) NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Inactive')),
    is_deleted  boolean NOT NULL DEFAULT false,
    row_version int NOT NULL DEFAULT 0,
    created_at  timestamptz NOT NULL DEFAULT now(),
    created_by  uuid,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid
);
-- Partial unique: a soft-deleted payer's code can be reused (repo convention — deleted rows never block a key).
CREATE UNIQUE INDEX IF NOT EXISTS uq_payer_code ON policy.payer (tenant_id, payer_code) WHERE NOT is_deleted;

-- ---- plan (the reusable product) -------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS policy.plan (
    plan_id     uuid PRIMARY KEY,
    tenant_id   text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',
    plan_code   varchar(30) NOT NULL,
    name_en     text NOT NULL,
    name_ar     text NOT NULL,
    description text,
    category    varchar(32) NOT NULL,
    status      varchar(16) NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Inactive')),
    is_deleted  boolean NOT NULL DEFAULT false,
    row_version int NOT NULL DEFAULT 0,
    created_at  timestamptz NOT NULL DEFAULT now(),
    created_by  uuid,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_plan_code ON policy.plan (tenant_id, plan_code) WHERE NOT is_deleted;

-- ---- plan_version (the heart of correctness) --------------------------------------------------------------
CREATE TABLE IF NOT EXISTS policy.plan_version (
    plan_version_id        uuid PRIMARY KEY,
    tenant_id              text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',
    plan_id                uuid NOT NULL REFERENCES policy.plan(plan_id),
    version_no             int NOT NULL CHECK (version_no > 0),
    effective_from         date NOT NULL,
    effective_to           date,                    -- EXCLUSIVE end; NULL = open-ended
    status                 varchar(16) NOT NULL DEFAULT 'Draft'
                             CHECK (status IN ('Draft','Active','Superseded','Retired')),
    activated_by           uuid,
    activated_at           timestamptz,
    superseded_by_version_id uuid REFERENCES policy.plan_version(plan_version_id),
    row_version            int NOT NULL DEFAULT 0,
    created_at             timestamptz NOT NULL DEFAULT now(),
    created_by             uuid,
    updated_at             timestamptz NOT NULL DEFAULT now(),
    updated_by             uuid,
    CONSTRAINT ck_plan_version_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
    -- An activated version must carry its activation signature; a draft must not claim one.
    CONSTRAINT ck_plan_version_activation CHECK (
        (status = 'Draft'  AND activated_at IS NULL AND activated_by IS NULL) OR
        (status <> 'Draft' AND activated_at IS NOT NULL)
    ),
    CONSTRAINT uq_plan_version_no UNIQUE (plan_id, version_no),
    -- No two RESOLVABLE versions of a plan may cover the same date — see the header note.
    CONSTRAINT ex_plan_version_no_overlap EXCLUDE USING gist (
        tenant_id WITH =,
        plan_id WITH =,
        daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[)') WITH &&
    ) WHERE (status <> 'Draft')
);
CREATE INDEX IF NOT EXISTS ix_plan_version_plan ON policy.plan_version (plan_id, effective_from DESC);
CREATE INDEX IF NOT EXISTS ix_plan_version_status ON policy.plan_version (status);

-- ---- benefit_rule (the benefit configuration, one row per category per version) ----------------------------
CREATE TABLE IF NOT EXISTS policy.benefit_rule (
    rule_id                uuid PRIMARY KEY,
    tenant_id              text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',
    plan_version_id        uuid NOT NULL REFERENCES policy.plan_version(plan_version_id) ON DELETE CASCADE,
    benefit_category_id    uuid NOT NULL REFERENCES policy.benefit_category(benefit_category_id),
    is_covered             boolean NOT NULL DEFAULT true,
    limit_type             varchar(16) CHECK (limit_type IN ('Annual','PerEncounter','Lifetime','Count')),
    limit_value            numeric(14,2) CHECK (limit_value IS NULL OR limit_value >= 0),
    reset_period           varchar(16) NOT NULL DEFAULT 'None'
                             CHECK (reset_period IN ('None','Monthly','Quarterly','Yearly')),
    copay_fixed            numeric(14,2) CHECK (copay_fixed IS NULL OR copay_fixed >= 0),
    copay_percent          numeric(5,2) CHECK (copay_percent IS NULL OR (copay_percent >= 0 AND copay_percent <= 100)),
    deductible             numeric(14,2) CHECK (deductible IS NULL OR deductible >= 0),
    waiting_period_days    int NOT NULL DEFAULT 0 CHECK (waiting_period_days >= 0),
    requires_preauth       boolean NOT NULL DEFAULT false,
    preauth_cost_threshold numeric(14,2) CHECK (preauth_cost_threshold IS NULL OR preauth_cost_threshold >= 0),
    network_tier           varchar(16),
    exclusions             jsonb NOT NULL DEFAULT '[]'::jsonb,
    notes                  text,
    created_at             timestamptz NOT NULL DEFAULT now(),
    created_by             uuid,
    updated_at             timestamptz NOT NULL DEFAULT now(),
    updated_by             uuid,
    -- A limit is a pair: a type without a value (or a value without a type) is not a rule, it is a bug.
    CONSTRAINT ck_benefit_rule_limit_pair CHECK ((limit_type IS NULL) = (limit_value IS NULL)),
    -- Fixed and percentage co-pay are alternatives; carrying both leaves the amount undefined at adjudication.
    CONSTRAINT ck_benefit_rule_copay CHECK (copay_fixed IS NULL OR copay_percent IS NULL),
    -- A cost threshold only means something when pre-auth is switched on.
    CONSTRAINT ck_benefit_rule_preauth CHECK (requires_preauth OR preauth_cost_threshold IS NULL),
    CONSTRAINT uq_benefit_rule_category UNIQUE (plan_version_id, benefit_category_id)
);
CREATE INDEX IF NOT EXISTS ix_benefit_rule_version ON policy.benefit_rule (plan_version_id);

-- ---- Immutability, enforced by the database ----------------------------------------------------------------
-- The API returns 409 on a write to an activated version, but "the API refuses" is not an invariant: a repair
-- script, a future endpoint or a direct psql session would walk straight through it. These triggers make the
-- rule structural, so an Active version's benefit configuration cannot be rewritten by ANY path.
--
-- The permitted transitions out of Active are Active→Superseded (with its effective_to closed and the successor
-- recorded) and Active→Retired. Everything else about the row is frozen.
CREATE OR REPLACE FUNCTION policy.guard_plan_version_immutable()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status = 'Draft' THEN
        RETURN NEW;                       -- drafts are freely editable, that is the point of a draft
    END IF;

    IF OLD.plan_id IS DISTINCT FROM NEW.plan_id
       OR OLD.version_no IS DISTINCT FROM NEW.version_no
       OR OLD.effective_from IS DISTINCT FROM NEW.effective_from
       OR OLD.activated_at IS DISTINCT FROM NEW.activated_at THEN
        RAISE EXCEPTION 'plan_version % is % and immutable: amend the plan to create a new version', OLD.plan_version_id, OLD.status
            USING ERRCODE = 'raise_exception';
    END IF;

    IF OLD.status = 'Active' AND NEW.status NOT IN ('Active','Superseded','Retired') THEN
        RAISE EXCEPTION 'plan_version % cannot leave Active for %', OLD.plan_version_id, NEW.status
            USING ERRCODE = 'raise_exception';
    END IF;
    IF OLD.status IN ('Superseded','Retired') AND NEW.status <> OLD.status THEN
        RAISE EXCEPTION 'plan_version % is % and cannot be reactivated', OLD.plan_version_id, OLD.status
            USING ERRCODE = 'raise_exception';
    END IF;
    -- effective_to may only be closed (set) at supersede time, never reopened or moved earlier than it was.
    IF OLD.effective_to IS NOT NULL AND NEW.effective_to IS DISTINCT FROM OLD.effective_to THEN
        RAISE EXCEPTION 'plan_version % already has a closed effective_to', OLD.plan_version_id
            USING ERRCODE = 'raise_exception';
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_plan_version_immutable ON policy.plan_version;
CREATE TRIGGER trg_plan_version_immutable BEFORE UPDATE ON policy.plan_version
    FOR EACH ROW EXECUTE FUNCTION policy.guard_plan_version_immutable();

-- A version's benefit configuration lives in benefit_rule, so freezing plan_version alone would freeze nothing
-- that matters. Rules are writable only while their parent version is a Draft.
CREATE OR REPLACE FUNCTION policy.guard_benefit_rule_immutable()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE parent_status text;
        parent_id uuid;
BEGIN
    parent_id := COALESCE(NEW.plan_version_id, OLD.plan_version_id);
    SELECT status INTO parent_status FROM policy.plan_version WHERE plan_version_id = parent_id;
    IF parent_status IS NOT NULL AND parent_status <> 'Draft' THEN
        RAISE EXCEPTION 'benefit rules of plan_version % are % and immutable: amend the plan to create a new version', parent_id, parent_status
            USING ERRCODE = 'raise_exception';
    END IF;
    RETURN COALESCE(NEW, OLD);
END $$;
DROP TRIGGER IF EXISTS trg_benefit_rule_immutable ON policy.benefit_rule;
CREATE TRIGGER trg_benefit_rule_immutable BEFORE INSERT OR UPDATE OR DELETE ON policy.benefit_rule
    FOR EACH ROW EXECUTE FUNCTION policy.guard_benefit_rule_immutable();

-- ---- Grants + tenant RLS (ADR-0011, same shape as 0002) ----------------------------------------------------
GRANT USAGE ON SCHEMA policy TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON policy.payer, policy.plan, policy.plan_version, policy.benefit_rule TO hbmp_app;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['payer','plan','plan_version','benefit_rule']
    LOOP
        EXECUTE format('ALTER TABLE policy.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE policy.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON policy.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON policy.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;
