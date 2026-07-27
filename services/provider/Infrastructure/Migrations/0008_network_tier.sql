-- provider-service — 0008 network tiers + effective-dated provider tier assignment
-- (phase 19.1b, design 38 §3 "network tiers (owned by provider-service)" and §4.1b). Additive + idempotent.
--
-- WHY THIS LIVES IN provider-service AND NOT policy-service. A tier is a COMMERCIAL statement about the
-- network — "this hospital is preferred, that one is out-of-network" — negotiated and owned by the Network
-- Team. What a member PAYS at a tier is a benefit-design statement, owned by policy administration
-- (policy.benefit_rule_tier). Keeping the two in different services with different scopes is the whole point
-- of 19.1b: a policy administrator may consume tiers when configuring cost-share but must not be able to move
-- a provider between them, because that silently reprices every plan at once.
--
-- HALF-OPEN WINDOWS. [effective_from, effective_to) — effective_to is EXCLUSIVE, matching policy.plan_version
-- (design 38 §7.1) so a tier move on 1 March means the old tier covers ..29 Feb and the new one covers 1 Mar..
-- with no gap and no doubly-covered day. NOTE this differs from provider.provider_contract (0001), which uses
-- an inclusive '[]' range; the two are not interchangeable and the difference is deliberate, not an oversight.

CREATE EXTENSION IF NOT EXISTS btree_gist;   -- uuid/text equality inside a GiST exclusion constraint

-- ---- network_tier ------------------------------------------------------------------------------------------
-- Reference data for the network: T1 preferred, T2 standard, OON out-of-network (or Gold/Silver/Bronze).
-- Tenant-scoped, never provider-scoped — a tier is a property of the network, not of one provider.
CREATE TABLE IF NOT EXISTS provider.network_tier (
    network_tier_id   uuid PRIMARY KEY,
    tenant_id         text NOT NULL,
    tier_code         varchar(12) NOT NULL,
    name_en           text NOT NULL,
    name_ar           text NOT NULL,
    rank              int NOT NULL CHECK (rank > 0),          -- 1 = most preferred
    description       text,
    is_out_of_network boolean NOT NULL DEFAULT false,
    status            varchar(12) NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Retired')),
    is_deleted        boolean NOT NULL DEFAULT false,
    row_version       int NOT NULL DEFAULT 0,
    created_at        timestamptz NOT NULL DEFAULT now(),
    created_by        text,
    updated_at        timestamptz NOT NULL DEFAULT now(),
    updated_by        text
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_network_tier_code
    ON provider.network_tier (tenant_id, tier_code) WHERE NOT is_deleted;
-- Rank orders the tiers for "most preferred"; two Active tiers sharing a rank makes that order undefined.
CREATE UNIQUE INDEX IF NOT EXISTS uq_network_tier_rank
    ON provider.network_tier (tenant_id, rank) WHERE status = 'Active' AND NOT is_deleted;
-- BEYOND THE BUILD PROMPT, and load-bearing: resolution FAILS SAFE to "the Active tier flagged
-- is_out_of_network". That fallback is only deterministic if exactly one such tier exists — with two, an
-- unassigned provider would be priced by whichever row the planner returned first. At most one is enforced
-- here rather than trusted to the endpoint.
CREATE UNIQUE INDEX IF NOT EXISTS uq_network_tier_single_oon
    ON provider.network_tier (tenant_id) WHERE is_out_of_network AND status = 'Active' AND NOT is_deleted;

-- ---- provider_network_assignment ---------------------------------------------------------------------------
-- Assigns a provider, one of its locations, or a single contract service line to a tier for a date window.
-- Most-specific-wins at resolution: ContractServiceLine > Location > Provider.
--
-- provider_id is DENORMALIZED from scope_ref (it is scope_ref itself for Provider scope, the location's parent
-- for Location scope, the line's contract's provider for ContractServiceLine scope). It exists so this table
-- can carry the same two-predicate RLS as every other provider-owned table below — without it a provider-scoped
-- session could read the whole network's tier map, which is commercially sensitive.
CREATE TABLE IF NOT EXISTS provider.provider_network_assignment (
    assignment_id   uuid PRIMARY KEY,
    tenant_id       text NOT NULL,
    network_tier_id uuid NOT NULL REFERENCES provider.network_tier(network_tier_id),
    provider_id     uuid NOT NULL REFERENCES provider.provider(provider_id),
    scope           varchar(20) NOT NULL CHECK (scope IN ('Provider','Location','ContractServiceLine')),
    scope_ref       uuid NOT NULL,
    effective_from  date NOT NULL,
    effective_to    date,                                     -- EXCLUSIVE end; NULL = open-ended
    status          varchar(12) NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Revoked')),
    revoked_reason  text,
    is_deleted      boolean NOT NULL DEFAULT false,
    row_version     int NOT NULL DEFAULT 0,
    created_at      timestamptz NOT NULL DEFAULT now(),
    created_by      text,
    updated_at      timestamptz NOT NULL DEFAULT now(),
    updated_by      text,
    CONSTRAINT ck_pna_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
    -- One tier per (scope, scope_ref) per day. Without this a provider could sit in T1 and T2 on the same
    -- date and the resolver would have two right answers — the same failure mode the plan_version exclusion
    -- prevents (policy/0005). Revoked rows are exempt: a revoked assignment never governed anything.
    CONSTRAINT ex_pna_no_overlap EXCLUDE USING gist (
        tenant_id WITH =,
        scope WITH =,
        scope_ref WITH =,
        daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[)') WITH &&
    ) WHERE (status = 'Active' AND NOT is_deleted)
);
CREATE INDEX IF NOT EXISTS ix_pna_scope ON provider.provider_network_assignment (scope, scope_ref, effective_from DESC);
CREATE INDEX IF NOT EXISTS ix_pna_provider ON provider.provider_network_assignment (provider_id);
CREATE INDEX IF NOT EXISTS ix_pna_tier ON provider.provider_network_assignment (network_tier_id);

-- ---- Grants + RLS (ADR-0011, same shape as 0003) ------------------------------------------------------------
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE
            ON provider.network_tier, provider.provider_network_assignment TO hbmp_app;
    END IF;
END $$;

-- network_tier: tenant-scoped only. Every routing decision in the platform is expressed in these codes, and a
-- provider user seeing that 'T2' exists learns nothing about anyone else's commercial position.
ALTER TABLE provider.network_tier ENABLE ROW LEVEL SECURITY;
ALTER TABLE provider.network_tier FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_network_tier ON provider.network_tier;
CREATE POLICY rls_network_tier ON provider.network_tier USING (
    tenant_id = current_setting('app.tenant_id', true)
);

-- provider_network_assignment: tenant AND provider predicate — WHICH tier a named provider sits in is exactly
-- the commercially sensitive part.
ALTER TABLE provider.provider_network_assignment ENABLE ROW LEVEL SECURITY;
ALTER TABLE provider.provider_network_assignment FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_provider_network_assignment ON provider.provider_network_assignment;
CREATE POLICY rls_provider_network_assignment ON provider.provider_network_assignment USING (
    tenant_id = current_setting('app.tenant_id', true)
    AND (
        coalesce(current_setting('app.provider_id', true), '') = ''
        OR provider_id::text = current_setting('app.provider_id', true)
    )
);

-- ---- 19.1b refinement: correction as a distinct third act ---------------------------------------------------
-- Withdrawing an assignment was two verbs and needed three. ENDING one closes effective_to and leaves it Active,
-- so it still governs its own window. REVOKING one that never took effect erases a statement that governed
-- nothing. Neither can fix the third case: an assignment that WAS in force and should never have been (wrong
-- provider, wrong tier). Without a correction verb, a week-old mis-assignment leaves a week of wrong tier
-- resolution standing with no legitimate way to repair it.
--
-- A correction retroactively voids the row. It is refused once any claim has adjudicated against that
-- assignment — at that point money has moved, and the fix is a claims adjustment, not a tier edit.
ALTER TABLE provider.provider_network_assignment DROP CONSTRAINT IF EXISTS provider_network_assignment_status_check;  -- migrate-compat: contract-ok (replaced immediately below by a WIDER check adding 'Corrected'; no existing value becomes invalid)
ALTER TABLE provider.provider_network_assignment
    ADD CONSTRAINT provider_network_assignment_status_check
    CHECK (status IN ('Active','Revoked','Corrected'));

-- Leaving Active is never silent: every one of the three acts records why.
ALTER TABLE provider.provider_network_assignment DROP CONSTRAINT IF EXISTS ck_pna_withdrawal_has_reason;  -- migrate-compat: contract-ok (idempotent drop-then-readd of a constraint this migration itself introduces)
ALTER TABLE provider.provider_network_assignment
    ADD CONSTRAINT ck_pna_withdrawal_has_reason
    CHECK (status = 'Active' OR revoked_reason IS NOT NULL);
