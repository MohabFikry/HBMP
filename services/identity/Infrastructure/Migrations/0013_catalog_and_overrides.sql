-- identity-service — 0013 catalog metadata + per-membership overrides (phase 21.2, design 40 §2, ADR-0021).
--
-- The authority model is four layers evaluated as set algebra:
--     effective = (role grants ∪ membership allows) − membership denies
-- 0011 made the role grants tenant-local. This migration adds the two layers that were missing: the
-- CATALOG metadata the evaluator needs (deprecation, and which keys platform-admin may short-circuit) and
-- the per-MEMBERSHIP override overlay — the exception path, with a reason and an optional expiry, so that
-- "this one nurse may also read orders, until the 30th, because X" stops being a bespoke role.
--
-- Additive + idempotent. Nothing here changes any existing principal's effective set: no override rows are
-- created, `deprecated` defaults false, and `is_platform_admin_key` defaults false so the A1 short-circuit
-- starts out matching NOTHING and is widened only by the explicit UPDATE below.

-- ---- Catalog metadata (design 40 §2 + §6) -------------------------------------------------------------------

ALTER TABLE identity.scope ADD COLUMN IF NOT EXISTS deprecated  boolean     NOT NULL DEFAULT false;
ALTER TABLE identity.scope ADD COLUMN IF NOT EXISTS replaced_by varchar(64);

-- A1 — this marks PLATFORM-ADMINISTRATION keys, and it is the ONLY thing the platform-admin flag may
-- short-circuit. It is deliberately NOT a wildcard: the evaluator hard-excludes every key without this
-- marker, so a platform administrator with no membership can administer the platform and still cannot read
-- a patient, a projected clinical field, a sensitive result, or a branch-scoped order list. Break-glass
-- stays the only elevation into clinical data.
ALTER TABLE identity.scope ADD COLUMN IF NOT EXISTS is_platform_admin_key boolean NOT NULL DEFAULT false;

-- `replaced_by` must name a real key, or an umbrella-split migration silently points consumers at nothing.
-- NOT VALID: existing rows are all NULL so there is nothing to check, and this keeps the DDL non-blocking.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_scope_replaced_by') THEN
        ALTER TABLE identity.scope
            ADD CONSTRAINT fk_scope_replaced_by FOREIGN KEY (replaced_by)
            REFERENCES identity.scope(name) ON DELETE SET NULL NOT VALID;
    END IF;
END $$;

-- The administration keys. Chosen by what they GOVERN, not by who tends to hold them: tenant management,
-- catalog management and identity administration. Nothing clinical, financial or benefit-shaped appears
-- here, and the A1 test pins that by asserting the complement is denied.
UPDATE identity.scope SET is_platform_admin_key = true
WHERE name IN ('admin:read', 'admin:write')
  AND is_platform_admin_key = false;

-- ---- Role trust tier (the ordinal axis, design 40 §2) -------------------------------------------------------
--
-- LOWER = MORE PRIVILEGED, seeded from 10-role-matrix §2's sensitivity tiers as level = 4 − tier, so the
-- T4 platform-critical personas land at 0. This answers ONLY tier-shaped questions (is this an
-- administrative persona: MFA-required tiers per 17, peer-review-required grants per 8b).
--
-- DISCIPLINE RULE (docs/CONVENTIONS.md, enforced in review): capability questions use KEYS, trust-tier
-- questions use LEVEL, and neither substitutes for the other. `level <= 1` is not a way to ask "can they
-- read EMR" — that question has a key, and answering it by tier is how a case manager quietly acquires a
-- doctor's reach.
ALTER TABLE identity.role ADD COLUMN IF NOT EXISTS level integer;

UPDATE identity.role SET level = v.level FROM (VALUES
    -- T4 — platform-critical: role bindings, policy, keys.
    ('super_admin', 0), ('org_admin', 0),
    -- T3 — PHI / clinical.
    ('doctor', 1), ('nurse', 1), ('lab_tech', 1), ('imaging_tech', 1), ('pharmacist', 1),
    ('medical_approval', 1), ('medical_director', 1), ('case_manager', 1),
    -- T2 — sensitive PII / financial.
    ('beneficiary_mgmt', 2), ('call_center', 2), ('finance', 2), ('provider_admin', 2),
    ('network_team', 2), ('claims_officer', 2),
    -- T1 — restricted PII.
    ('reception', 3)
) AS v(name, level)
WHERE identity.role.normalized_name = upper(v.name) AND identity.role.level IS DISTINCT FROM v.level;

-- ---- Per-membership overrides (design 40 §2) ----------------------------------------------------------------
--
-- The exception path, attached to the MEMBERSHIP because that is the principal (invariant 1). Every row
-- carries a REASON, because an unexplained exception is indistinguishable from a mistake at review time,
-- and an optional expiry so a temporary grant expires by itself rather than by someone remembering.
--
-- Expiry is evaluated AT RESOLUTION TIME — there is no sweeper. An override past its valid_until simply
-- stops matching, which means a missed job can never leave access switched on.
CREATE TABLE IF NOT EXISTS identity.membership_override (
    override_id   uuid PRIMARY KEY,
    membership_id uuid        NOT NULL REFERENCES identity.tenant_membership(membership_id) ON DELETE CASCADE,
    scope_key     varchar(64) NOT NULL REFERENCES identity.scope(name) ON DELETE CASCADE,
    effect        varchar(5)  NOT NULL CHECK (effect IN ('Allow', 'Deny')),
    reason        varchar(300) NOT NULL,
    granted_by    text,
    valid_until   timestamptz,
    -- House style: soft delete + optimistic concurrency + attribution (CLAUDE.md § Audit — no hard deletes).
    is_deleted    boolean     NOT NULL DEFAULT false,
    row_version   integer     NOT NULL DEFAULT 0,
    created_by    text,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_by    text,
    updated_at    timestamptz NOT NULL DEFAULT now()
);

-- One live override per (membership, key). Two rows for the same key would make "the effect" ambiguous;
-- deny-wins would still give a safe answer, but the ADMIN UI could not show a single truthful state, and
-- revoking an Allow would silently leave a second Allow behind.
CREATE UNIQUE INDEX IF NOT EXISTS ux_override_membership_scope
    ON identity.membership_override (membership_id, scope_key) WHERE NOT is_deleted;

CREATE INDEX IF NOT EXISTS ix_override_membership
    ON identity.membership_override (membership_id) WHERE NOT is_deleted;

CREATE TABLE IF NOT EXISTS identity.membership_override_history (
    history_id    bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    override_id   uuid        NOT NULL,
    membership_id uuid        NOT NULL,
    scope_key     varchar(64) NOT NULL,
    effect        varchar(5)  NOT NULL,
    reason        varchar(300) NOT NULL,
    valid_until   timestamptz,
    is_deleted    boolean     NOT NULL,
    row_version   integer     NOT NULL,
    changed_by    text,
    changed_at    timestamptz NOT NULL DEFAULT now(),
    change_reason text
);
CREATE INDEX IF NOT EXISTS ix_override_history_override
    ON identity.membership_override_history (override_id, changed_at DESC);

-- ---- Grants (0002's model) ----------------------------------------------------------------------------------

GRANT SELECT, INSERT, UPDATE, DELETE ON identity.membership_override         TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON identity.membership_override_history TO hbmp_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA identity TO hbmp_app;
