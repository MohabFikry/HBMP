-- ============================================================================================================
-- seed-provider-bound-accounts.sql — every provider-scoped login, bound to a provider that exists.
--
--   psql -h localhost -p 55432 -U hbmp -d hbmp -v ON_ERROR_STOP=1 -f tools/dev/seed-provider-bound-accounts.sql
--
-- Run AFTER restore-reference-structure.sql and seed-dev-clinic.sql. Idempotent.
--
-- ============================================================================================================
-- THE DEFECT: A LOGIN CONFIGURED TO WORK NOWHERE
-- ============================================================================================================
-- Several roles are provider-scoped by design — a pharmacist dispenses for their pharmacy, a lab tech fulfils
-- for their lab — and every gate guarding them opens the same way:
--
--     if (string.IsNullOrWhiteSpace(p.ProviderId))
--         return Deny("You are not associated with a dispensing pharmacy.");
--         -- pharmacy/Api/DispensingGate.cs, orders/Api/FulfillmentGate.cs
--
-- The claim comes from `identity.tenant_membership.provider_id`, and the base seed leaves it NULL for every
-- account. So these logins authenticate perfectly, receive every scope their role grants, and are then
-- refused every screen in their own portal. The refusal is accurate and reads as a permissions bug, which is
-- why FOUR roles sat in that state simultaneously without anyone noticing.
--
-- WHY ONE FILE AND NOT ONE PER ROLE. This began as a fix for the pharmacist, then the same fix for the lab and
-- imaging techs. `provider_admin` was found only when identity-service gained a startup check that derives the
-- provider-scoped roles from the authorization rules themselves (libs/authz/ProviderScopedRoles.cs) and named
-- the one nobody had thought of. Three near-identical files were how the fourth stayed hidden; the set is one
-- thing, so it is seeded in one place. Keep this file aligned with that derivation — if the check reports a
-- role that is not handled below, add it here rather than starting a fourth file.
--
-- WHY NEW PROVIDERS RATHER THAN REUSING DELTA DIAGNOSTICS LAB. PRV-0003 is a Lab and would satisfy the gate,
-- which checks ownership and not status. It is also deliberately SUSPENDED in restore-reference-structure.sql
-- — the environment's only suspended provider, and therefore the only fixture for every "not currently
-- contracted" path. Binding a working technician to it would spend that fixture and leave the data claiming a
-- suspended provider with staff actively fulfilling orders. Delta stays exactly as it is.
--
-- SYNTHETIC. Every provider and the order below are invented. CLAUDE.md: never real PHI in lower environments.
-- ============================================================================================================

\set ON_ERROR_STOP on

SET app.tenant_id = '11111111-1111-1111-1111-111111111111';

BEGIN;

-- ── The providers ───────────────────────────────────────────────────────────────────────────────────────────
-- PRV-0004..0006 continue the series restore-reference-structure.sql starts at PRV-0001.
INSERT INTO provider.provider (provider_id, tenant_id, provider_code, legal_name, provider_type, status, onboarding_state) VALUES
  ('b0000000-0000-4000-8000-000000000004', '11111111-1111-1111-1111-111111111111', 'PRV-0004', 'Nile Pharmacy',            'Pharmacy', 'Active', 'Activated'),
  ('b0000000-0000-4000-8000-000000000005', '11111111-1111-1111-1111-111111111111', 'PRV-0005', 'Cairo Central Laboratory', 'Lab',      'Active', 'Activated'),
  ('b0000000-0000-4000-8000-000000000006', '11111111-1111-1111-1111-111111111111', 'PRV-0006', 'Nile Imaging Centre',      'Imaging',  'Active', 'Activated')
ON CONFLICT (provider_id) DO UPDATE
  SET legal_name = EXCLUDED.legal_name, provider_type = EXCLUDED.provider_type, status = EXCLUDED.status;

INSERT INTO provider.provider_location (location_id, provider_id, tenant_id, name, governorate, address, is_primary) VALUES
  ('b1000000-0000-4000-8000-000000000004', 'b0000000-0000-4000-8000-000000000004', '11111111-1111-1111-1111-111111111111', 'Dispensary',    'Cairo', '19 Ramses St',       true),
  ('b1000000-0000-4000-8000-000000000005', 'b0000000-0000-4000-8000-000000000005', '11111111-1111-1111-1111-111111111111', 'Main Lab',      'Cairo', '31 Qasr El Aini St', true),
  ('b1000000-0000-4000-8000-000000000006', 'b0000000-0000-4000-8000-000000000006', '11111111-1111-1111-1111-111111111111', 'Imaging Suite', 'Cairo', '8 Gameat El Dowal',  true)
ON CONFLICT (location_id) DO UPDATE SET name = EXCLUDED.name, address = EXCLUDED.address;

-- In the contracted network, so coverage and pricing resolve to a tier rather than falling through to
-- out-of-network — the same treatment the hospital and the clinic already get.
INSERT INTO provider.provider_network_assignment
  (assignment_id, tenant_id, network_tier_id, provider_id, scope, scope_ref, effective_from, status) VALUES
  ('a4d4c1f0-0000-4000-8000-000000000004', '11111111-1111-1111-1111-111111111111', 'f1c08cbb-38ad-4dad-89e0-22124dc4a89b', 'b0000000-0000-4000-8000-000000000004', 'Provider', 'b0000000-0000-4000-8000-000000000004', '2026-01-01', 'Active'),
  ('a4d4c1f0-0000-4000-8000-000000000005', '11111111-1111-1111-1111-111111111111', 'f1c08cbb-38ad-4dad-89e0-22124dc4a89b', 'b0000000-0000-4000-8000-000000000005', 'Provider', 'b0000000-0000-4000-8000-000000000005', '2026-01-01', 'Active'),
  ('a4d4c1f0-0000-4000-8000-000000000006', '11111111-1111-1111-1111-111111111111', 'f1c08cbb-38ad-4dad-89e0-22124dc4a89b', 'b0000000-0000-4000-8000-000000000006', 'Provider', 'b0000000-0000-4000-8000-000000000006', '2026-01-01', 'Active')
ON CONFLICT (assignment_id) DO UPDATE SET status = 'Active';

-- ── Bind the logins ─────────────────────────────────────────────────────────────────────────────────────────
-- `provider_admin` administers an EXISTING organisation rather than a new one: its portal is scoped to the
-- provider it belongs to (`provider:read-own`), so the interesting fixture is the flagship hospital with
-- locations, practitioners and contracts already attached — not an empty fourth site.
--
-- Fails loudly rather than reporting success over a no-op, which would reproduce the exact confusion this
-- file exists to remove.
DO $$
DECLARE
    r       record;
    uid     uuid;
    bound   int;
BEGIN
    FOR r IN
        SELECT * FROM (VALUES
            ('pharmacist',     'b0000000-0000-4000-8000-000000000004'::uuid, 'Nile Pharmacy (PRV-0004)'),
            ('lab_tech',       'b0000000-0000-4000-8000-000000000005'::uuid, 'Cairo Central Laboratory (PRV-0005)'),
            ('imaging_tech',   'b0000000-0000-4000-8000-000000000006'::uuid, 'Nile Imaging Centre (PRV-0006)'),
            ('provider_admin', 'b0000000-0000-4000-8000-000000000001'::uuid, 'Nile Central Hospital (PRV-0001)')
        ) AS t(login, provider, label)
    LOOP
        SELECT id INTO uid FROM identity."user" WHERE user_name = r.login;
        IF uid IS NULL THEN
            RAISE EXCEPTION 'no identity user named "%" — start identity-service once so UserSeeder runs', r.login;
        END IF;

        UPDATE identity.tenant_membership
           SET provider_id = r.provider
         WHERE user_id = uid
           AND tenant_id = '11111111-1111-1111-1111-111111111111';
        GET DIAGNOSTICS bound = ROW_COUNT;

        IF bound = 0 THEN
            RAISE EXCEPTION '% has no membership in this tenant — nothing to bind a provider to', r.login;
        END IF;

        -- The identity-level column too: it is the legacy path UserClaimsService falls back to for a user
        -- with no membership resolved, and two columns disagreeing means the answer depends on which ran.
        UPDATE identity."user" SET provider_id = r.provider WHERE id = uid;

        RAISE NOTICE '% bound to %', r.login, r.label;
    END LOOP;
END $$;

-- ── One Imaging order, so the imaging queue is not empty on arrival ─────────────────────────────────────────
-- There were no Imaging orders at all, so binding alone would have moved `imaging_tech` from a 403 to a blank
-- screen — not visibly different from still being broken. The queue matches on CAPABILITY (lab_tech → Lab,
-- imaging_tech → Imaging; see ProviderCapability.ForRoles), not on the fulfilling provider, so this order
-- needs no provider of its own. It hangs off a REAL encounter, so the screens that resolve the patient behind
-- it have something to resolve.
DO $$
DECLARE
    enc     uuid;
    ben     uuid;
    branch  uuid;
    author  uuid;
BEGIN
    SELECT e.encounter_id, e.beneficiary_id INTO enc, ben
      FROM emr.encounter e ORDER BY e.started_at DESC LIMIT 1;

    IF enc IS NULL THEN
        RAISE EXCEPTION 'no encounters exist — run seed-dev-clinic.sql (and seed-doctor-account.sql) first';
    END IF;

    SELECT created_by, ordering_branch_id INTO author, branch
      FROM orders.investigation_order ORDER BY requested_at DESC LIMIT 1;
    IF author IS NULL THEN
        SELECT id INTO author FROM identity."user" WHERE user_name = 'doctor';
    END IF;

    -- ORD-2026-000900 sits well above the live counter, and the counter is pushed past it below, so a real
    -- order issued later cannot collide with this fixture.
    INSERT INTO orders.investigation_order
        (order_id, order_no, beneficiary_id, encounter_id, ordering_provider_id, order_type, status,
         requested_at, expires_at, idempotency_key, created_by, ordering_branch_id, sensitivity_level, tenant_id)
    VALUES
        ('0d900000-0000-4000-8000-000000000001', 'ORD-2026-000900', ben, enc,
         '00000000-0000-0000-0000-000000000000', 'Imaging', 'Active',
         now() - interval '2 hours', now() + interval '30 days',
         'seed:imaging:ORD-2026-000900', author, branch, 'Standard',
         '11111111-1111-1111-1111-111111111111')
    ON CONFLICT (order_id) DO UPDATE
        SET status = 'Active', beneficiary_id = EXCLUDED.beneficiary_id, encounter_id = EXCLUDED.encounter_id;

    INSERT INTO orders.order_line
        (order_line_id, order_id, code_system, code, description, quantity_ordered, quantity_consumed,
         status, examination_type_id, sensitivity_level, tenant_id)
    VALUES
        ('0d900000-0000-4000-8000-000000000002', '0d900000-0000-4000-8000-000000000001',
         'CPT', '71046', 'Chest X-Ray', 1, 0, 'Active',
         '0190c100-0000-7000-8000-000000000003', 'Standard', '11111111-1111-1111-1111-111111111111')
    ON CONFLICT (order_line_id) DO UPDATE
        SET status = 'Active', quantity_consumed = 0;

    UPDATE orders.order_seq SET last_value = GREATEST(last_value, 900) WHERE year = 2026;

    RAISE NOTICE 'ORD-2026-000900 (Chest X-Ray) queued for imaging on encounter %', enc;
END $$;

COMMIT;

-- Claims are stamped at sign-in: a session opened BEFORE this ran still carries no provider_id. Sign out and
-- back in before expecting these portals to answer.
--
-- To confirm: restart identity-service and read its log. The provider-binding check reports either
-- "every active membership holding a provider-scoped role is bound" or names the ones that are not.
