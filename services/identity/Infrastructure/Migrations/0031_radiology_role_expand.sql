-- identity-service — 0031 EXPAND: add `radiology_tech` beside `imaging_tech`. Additive; removes nothing.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- 29.1 / design 45 §1 — "Radiology" replaces "Imaging" everywhere, IDENTIFIERS INCLUDED. This is the EXPAND
-- step of expand → backfill → switch → contract (0031 here, 0032 backfill, the switch is code, and the
-- contract lives in Migrations/deferred/ so it CANNOT ship on the same deploy — see
-- docs/runbooks/radiology-rename.md).
--
-- Both role names exist after this migration and both carry the same scopes. That is not indecision: a role
-- rename is not atomic on a running platform, because an access token minted one second before the switch
-- names the OLD role and stays valid for the rest of its 300 s TTL (token-contract.md §4), and because
-- services deploy independently, so the switched issuer mints the NEW name at a service that has not been
-- redeployed yet. libs/auth/LegacyRoleAliases.cs makes a principal answer to both spellings for the duration;
-- this migration makes the STORE able to grant either one.
--
-- SCOPES ARE COPIED, NOT RETYPED. Retyping the list is how the two roles drift: `imaging_tech` has picked up
-- six scopes across migrations 0005, 0009, 0024, 0026 and 0030, and a hand-written list here would freeze
-- today's set and silently omit tomorrow's. Copying from the live rows also means this migration stays correct
-- if it is applied to a tenant that granted extra scopes locally through the 17.4 admin surface.
--
-- NOTE ON THE SCOPE VOCABULARY. Design 45 §1's table says `imaging:*` → `radiology:*`, but no OAuth scope on
-- this platform is spelled `imaging:*` — the technician's capabilities are `orders:read` / `orders:consume`,
-- which are ORDER scopes shared with the lab bench and are not renamed. The `imaging.*` strings that DO exist
-- are the SPA's client-side permission keys (apps/web/src/authz/permissions.ts), renamed in the switch commit.
-- Recorded in ADR-0029 rather than silently resolved.

-- ---- The role itself, at the same sensitivity tier (T3 — mirrors services/admin/Domain/RoleCatalog.cs) -----

INSERT INTO identity.role (id, name, normalized_name, concurrency_stamp, sensitivity_tier)
SELECT gen_random_uuid(), 'radiology_tech', 'RADIOLOGY_TECH', gen_random_uuid()::text, r.sensitivity_tier
FROM identity.role r
WHERE r.name = 'imaging_tech'
ON CONFLICT (normalized_name) DO NOTHING;

-- ---- Every scope imaging_tech holds, in every tenant that grants it ----------------------------------------

INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT rs.tenant_id, 'radiology_tech', rs.scope_name
FROM identity.role_scope rs
WHERE rs.role_name = 'imaging_tech'
ON CONFLICT DO NOTHING;

-- ---- Assert the two roles are scope-identical -------------------------------------------------------------
-- A rename that quietly changes authority is a privilege change wearing a rename's clothes. Fail the
-- migration rather than discover it from a 403 in production.

DO $$
DECLARE drift int;
BEGIN
    SELECT count(*) INTO drift FROM (
        SELECT tenant_id, scope_name FROM identity.role_scope WHERE role_name = 'imaging_tech'
        EXCEPT
        SELECT tenant_id, scope_name FROM identity.role_scope WHERE role_name = 'radiology_tech'
    ) d;
    IF drift > 0 THEN
        RAISE EXCEPTION 'radiology_tech is missing % scope grant(s) that imaging_tech holds', drift;
    END IF;
END $$;
