-- ============================================================================================================
-- restore-reference-structure.sql — the branches, providers, network tiers and practitioners the platform
-- already had. NOT invented here: these rows are transcribed from the environment as it stood before the
-- 2026-08 data reset, with the test-tenant junk left out.
--
--   psql -h localhost -p 55432 -U hbmp -d hbmp -v ON_ERROR_STOP=1 -f tools/dev/restore-reference-structure.sql
--
-- WHY THIS FILE EXISTS. The first pass at re-seeding invented its own branches — `BR-DOK` / "Mersal Dokki",
-- with ids derived from an md5 — and threw away six branches that already had clean codes (DOK, NSR, MAA, ALX,
-- OCT, ASW), proper Arabic names and deliberately-shaped uuids. Structure is not test payload. Screenshots,
-- bug reports, saved URLs and the frontend fixtures all refer to these ids, and regenerating them breaks every
-- one of those for no gain.
--
-- The rule this encodes: RESET THE BUSINESS DATA, KEEP THE ORGANISATION. A branch is not a test fixture.
--
-- WHAT WAS DELIBERATELY DROPPED — rows belonging to throwaway tenants created by test runs, which had leaked
-- into the shared dev database:
--   provider           "Test Hospital"  tenant tier-test-247b6f35b22147ccb0037b2020e584ee
--   network_tier       T1 + OON         same tenant (duplicates of the real pair below)
--   practitioner       "Dr Test"        tenant t-f414ff41c0
-- ============================================================================================================

\set ON_ERROR_STOP on
SET app.tenant_id = '11111111-1111-1111-1111-111111111111';

BEGIN;

-- ── Branches ────────────────────────────────────────────────────────────────────────────────────────────────
-- Six real Mersal locations. Codes are the three-letter city abbreviations already in use.
INSERT INTO provider.branch (branch_id, branch_code, name_en, name_ar, city, timezone, status) VALUES
  ('0190b100-0000-7000-8000-000000000001', 'ASW', 'Aswan',          'أسوان',            'Aswan',      'Africa/Cairo', 'Active'),
  ('0190b100-0000-7000-8000-000000000002', 'ALX', 'Alexandria',     'الإسكندرية',       'Alexandria', 'Africa/Cairo', 'Active'),
  ('0190b100-0000-7000-8000-000000000003', 'OCT', '6th of October', 'السادس من أكتوبر', 'Giza',       'Africa/Cairo', 'Active'),
  ('0190b100-0000-7000-8000-000000000004', 'MAA', 'Maadi',          'المعادي',          'Cairo',      'Africa/Cairo', 'Active'),
  ('0190b100-0000-7000-8000-000000000005', 'DOK', 'Dokki',          'الدقي',            'Giza',       'Africa/Cairo', 'Active'),
  ('0190b100-0000-7000-8000-000000000006', 'NSR', 'Nasr City',      'مدينة نصر',        'Cairo',      'Africa/Cairo', 'Active')
ON CONFLICT (branch_id) DO UPDATE
  SET branch_code = EXCLUDED.branch_code, name_en = EXCLUDED.name_en, name_ar = EXCLUDED.name_ar,
      city = EXCLUDED.city, status = EXCLUDED.status;

-- ── Providers and their locations ───────────────────────────────────────────────────────────────────────────
-- Delta Diagnostics is Suspended on purpose: a network with every provider Active never exercises the
-- suspended-provider path, and that path decides whether a referral can be sent.
INSERT INTO provider.provider (provider_id, tenant_id, provider_code, legal_name, provider_type, status, onboarding_state) VALUES
  ('b0000000-0000-4000-8000-000000000001', '11111111-1111-1111-1111-111111111111', 'PRV-0001', 'Nile Central Hospital',  'Hospital', 'Active',    'Activated'),
  ('b0000000-0000-4000-8000-000000000002', '11111111-1111-1111-1111-111111111111', 'PRV-0002', 'Cairo Care Clinic',      'Clinic',   'Active',    'Activated'),
  ('b0000000-0000-4000-8000-000000000003', '11111111-1111-1111-1111-111111111111', 'PRV-0003', 'Delta Diagnostics Lab',  'Lab',      'Suspended', 'Credentialed')
ON CONFLICT (provider_id) DO UPDATE
  SET legal_name = EXCLUDED.legal_name, provider_type = EXCLUDED.provider_type, status = EXCLUDED.status;

INSERT INTO provider.provider_location (location_id, provider_id, tenant_id, name, governorate, address, is_primary) VALUES
  ('b1000000-0000-4000-8000-000000000001', 'b0000000-0000-4000-8000-000000000001', '11111111-1111-1111-1111-111111111111', 'Main Campus', 'Cairo', '12 Nile Corniche', true),
  ('b1000000-0000-4000-8000-000000000002', 'b0000000-0000-4000-8000-000000000001', '11111111-1111-1111-1111-111111111111', 'East Annex',  'Cairo', '4 Salah Salem St', false),
  ('b1000000-0000-4000-8000-000000000003', 'b0000000-0000-4000-8000-000000000002', '11111111-1111-1111-1111-111111111111', 'Downtown',    'Cairo', '7 Tahrir Sq',      true)
ON CONFLICT (location_id) DO UPDATE SET name = EXCLUDED.name, address = EXCLUDED.address;

-- ── Network tiers ───────────────────────────────────────────────────────────────────────────────────────────
-- The descriptions are the originals; they explain why an out-of-network tier exists at all.
INSERT INTO provider.network_tier (network_tier_id, tenant_id, tier_code, name_en, name_ar, rank, description, is_out_of_network, status) VALUES
  ('f1c08cbb-38ad-4dad-89e0-22124dc4a89b', '11111111-1111-1111-1111-111111111111', 'T1',  'Tier 1 — contracted network', 'الشريحة الأولى — الشبكة المتعاقدة', 1,
   'Every provider Mersal holds a contract with. Split into finer tiers through the network administration screen when the commercial terms differ.', false, 'Active'),
  ('420ba5d3-d92b-4325-b922-61d6b047e150', '11111111-1111-1111-1111-111111111111', 'OON', 'Out of network',              'خارج الشبكة',                      99,
   'No contract. Exists so the resolver has an answer and so leakage has a denominator.', true, 'Active')
ON CONFLICT (network_tier_id) DO UPDATE SET name_en = EXCLUDED.name_en, description = EXCLUDED.description;

INSERT INTO provider.provider_network_assignment
  (assignment_id, tenant_id, network_tier_id, provider_id, scope, scope_ref, effective_from, status) VALUES
  ('a1d09b4e-dadf-4e4a-9852-6e674bf3441d', '11111111-1111-1111-1111-111111111111', 'f1c08cbb-38ad-4dad-89e0-22124dc4a89b', 'b0000000-0000-4000-8000-000000000001', 'Provider', 'b0000000-0000-4000-8000-000000000001', '2026-01-01', 'Active'),
  ('a6a3094f-4ecf-436a-a594-a3fc5c94793c', '11111111-1111-1111-1111-111111111111', 'f1c08cbb-38ad-4dad-89e0-22124dc4a89b', 'b0000000-0000-4000-8000-000000000002', 'Provider', 'b0000000-0000-4000-8000-000000000002', '2026-01-01', 'Active'),
  ('a222cce6-d0a6-42d7-837d-db49536c75cf', '11111111-1111-1111-1111-111111111111', 'f1c08cbb-38ad-4dad-89e0-22124dc4a89b', 'b0000000-0000-4000-8000-000000000003', 'Provider', 'b0000000-0000-4000-8000-000000000003', '2026-07-25', 'Active')
ON CONFLICT (assignment_id) DO UPDATE SET status = 'Active';

-- ── Practitioners ───────────────────────────────────────────────────────────────────────────────────────────
-- Two generations, both real and both kept. The `demo-dr-*` three are the ones the frontend fixtures name
-- (Hana Mansour / Youssef Adel / Mona Saleh), so renaming them would break tests that read the screen.
--
-- Dr Omar Adel's licence expired on 2026-07-25 — in the PAST, and left that way deliberately: the licence
-- gate is only testable against a practitioner who fails it.
INSERT INTO provider.practitioner
  (practitioner_id, tenant_id, user_id, practitioner_type, full_name_en, full_name_ar, license_no, license_expiry, status) VALUES
  ('25000000-0000-0000-0000-00000000e001', '11111111-1111-1111-1111-111111111111', 'seed-dr-hala',    'Doctor', 'Dr Hala Fouad',  'د. هالة فؤاد',  'SEED-LIC-0001', '2026-08-21', 'Active'),
  ('25000000-0000-0000-0000-00000000e002', '11111111-1111-1111-1111-111111111111', 'seed-dr-omar',    'Doctor', 'Dr Omar Adel',   'د. عمر عادل',   'SEED-LIC-0002', '2026-07-25', 'Active'),
  ('25000000-0000-0000-0000-00000000e003', '11111111-1111-1111-1111-111111111111', 'seed-dr-mona',    'Doctor', 'Dr Mona Saleh',  'د. منى صالح',   'SEED-LIC-0003', '2027-09-05', 'Active'),
  ('0190d0c0-0000-7000-8000-000000000001', '11111111-1111-1111-1111-111111111111', 'demo-dr-hana',    'Doctor', 'Hana Mansour',   'هناء منصور',    'LIC-10001',     '2027-06-30', 'Active'),
  ('0190d0c0-0000-7000-8000-000000000002', '11111111-1111-1111-1111-111111111111', 'demo-dr-youssef', 'Doctor', 'Youssef Adel',   'يوسف عادل',     'LIC-10002',     '2027-11-30', 'Active'),
  ('0190d0c0-0000-7000-8000-000000000003', '11111111-1111-1111-1111-111111111111', 'demo-dr-mona',    'Doctor', 'Mona Saleh',     'منى صالح',      'LIC-10003',     '2028-03-31', 'Active')
ON CONFLICT (practitioner_id) DO UPDATE
  SET full_name_en = EXCLUDED.full_name_en, full_name_ar = EXCLUDED.full_name_ar,
      license_no = EXCLUDED.license_no, license_expiry = EXCLUDED.license_expiry;

INSERT INTO provider.practitioner_specialty (practitioner_id, specialty_code, is_primary) VALUES
  ('25000000-0000-0000-0000-00000000e001', 'GP',    true),
  ('25000000-0000-0000-0000-00000000e002', 'IM',    true),
  ('25000000-0000-0000-0000-00000000e003', 'PED',   true),
  ('0190d0c0-0000-7000-8000-000000000001', 'PED',   true),
  ('0190d0c0-0000-7000-8000-000000000002', 'CARD',  true),
  ('0190d0c0-0000-7000-8000-000000000003', 'OBGYN', true)
ON CONFLICT (practitioner_id, specialty_code) DO NOTHING;

INSERT INTO provider.practitioner_branch_assignment (assignment_id, practitioner_id, branch_id, valid_from, status) VALUES
  ('3f289b6b-51c9-41d8-b1a9-dd24cf978c5a', '25000000-0000-0000-0000-00000000e001', '0190b100-0000-7000-8000-000000000004', '2025-08-01', 'Active'), -- Hala    → Maadi
  ('044a2f95-e49f-459b-8635-6c9563770098', '25000000-0000-0000-0000-00000000e002', '0190b100-0000-7000-8000-000000000004', '2025-08-01', 'Active'), -- Omar    → Maadi
  ('18e2ef16-98d1-41c0-b64d-782d89d30f1c', '25000000-0000-0000-0000-00000000e003', '0190b100-0000-7000-8000-000000000005', '2025-08-01', 'Active'), -- Mona S. → Dokki
  ('0190d0b0-0000-7000-8000-000000000001', '0190d0c0-0000-7000-8000-000000000001', '0190b100-0000-7000-8000-000000000005', '2026-01-01', 'Active'), -- Hana    → Dokki
  ('0190d0b0-0000-7000-8000-000000000002', '0190d0c0-0000-7000-8000-000000000002', '0190b100-0000-7000-8000-000000000005', '2026-01-01', 'Active'), -- Youssef → Dokki
  ('0190d0b0-0000-7000-8000-000000000003', '0190d0c0-0000-7000-8000-000000000003', '0190b100-0000-7000-8000-000000000005', '2026-01-01', 'Active')  -- Mona    → Dokki
ON CONFLICT (assignment_id) DO UPDATE SET status = 'Active';

-- Nasr City had no practitioners assigned. Youssef also works there, so the branch picker is a real choice
-- rather than one branch and five empty ones.
INSERT INTO provider.practitioner_branch_assignment (assignment_id, practitioner_id, branch_id, valid_from, status) VALUES
  ('0190d0b0-0000-7000-8000-000000000004', '0190d0c0-0000-7000-8000-000000000002', '0190b100-0000-7000-8000-000000000006', '2026-01-01', 'Active')
ON CONFLICT (assignment_id) DO UPDATE SET status = 'Active';

-- ── Clinic rosters ──────────────────────────────────────────────────────────────────────────────────────────
-- The originals: Nile Central at Dokki mornings (09:00–13:00, 20 min) and Nasr City afternoons
-- (14:00–17:00, 30 min), every day of the week, with NO doctor named — generic clinic capacity.
INSERT INTO emr.provider_availability
  (availability_id, provider_id, location_id, doctor_id, day_of_week, start_time, end_time, slot_minutes, branch_id, tenant_id)
SELECT ('0190a000-0000-7000-8000-0000000000' || lpad((r.dow + 1)::text, 2, '0'))::uuid,
       'b0000000-0000-4000-8000-000000000001', 'b1000000-0000-4000-8000-000000000001', NULL,
       r.dow, TIME '09:00', TIME '13:00', 20,
       '0190b100-0000-7000-8000-000000000005', '11111111-1111-1111-1111-111111111111'
FROM generate_series(0, 6) AS r(dow)
ON CONFLICT (availability_id) DO NOTHING;

INSERT INTO emr.provider_availability
  (availability_id, provider_id, location_id, doctor_id, day_of_week, start_time, end_time, slot_minutes, branch_id, tenant_id)
SELECT ('0190a000-0000-7000-8000-0000000001' || lpad((r.dow + 1)::text, 2, '0'))::uuid,
       'b0000000-0000-4000-8000-000000000001', 'b1000000-0000-4000-8000-000000000002', NULL,
       r.dow, TIME '14:00', TIME '17:00', 30,
       '0190b100-0000-7000-8000-000000000006', '11111111-1111-1111-1111-111111111111'
FROM generate_series(0, 6) AS r(dow)
ON CONFLICT (availability_id) DO NOTHING;

-- DOCTOR-SPECIFIC clinics, added on top. The originals name no doctor, which is correct for generic capacity
-- but leaves the booking form's branch → specialty → doctor → time chain with nothing to resolve: pick a
-- specialty and there is no doctor to choose. These give each named practitioner their own consulting hours.
-- Saturday–Thursday, closed Friday (dow 5), which is the Egyptian working week.
INSERT INTO emr.provider_availability
  (availability_id, provider_id, location_id, doctor_id, day_of_week, start_time, end_time, slot_minutes, branch_id, tenant_id)
SELECT ('0190a001-0000-7000-8000-0000' || lpad(d.seq::text, 4, '0') || lpad(dow::text, 4, '0'))::uuid,
       d.provider_id, d.location_id, d.practitioner_id,
       dow, d.starts, d.ends, 20, d.branch_id, '11111111-1111-1111-1111-111111111111'
FROM (VALUES
  -- Dokki (Cairo Care Clinic, Downtown location) — the branch the frontend fixtures book into.
  (1, '0190d0c0-0000-7000-8000-000000000001'::uuid, 'b0000000-0000-4000-8000-000000000002'::uuid, 'b1000000-0000-4000-8000-000000000003'::uuid, '0190b100-0000-7000-8000-000000000005'::uuid, TIME '09:00', TIME '15:00'),
  (2, '0190d0c0-0000-7000-8000-000000000002'::uuid, 'b0000000-0000-4000-8000-000000000002'::uuid, 'b1000000-0000-4000-8000-000000000003'::uuid, '0190b100-0000-7000-8000-000000000005'::uuid, TIME '10:00', TIME '16:00'),
  (3, '0190d0c0-0000-7000-8000-000000000003'::uuid, 'b0000000-0000-4000-8000-000000000002'::uuid, 'b1000000-0000-4000-8000-000000000003'::uuid, '0190b100-0000-7000-8000-000000000005'::uuid, TIME '09:00', TIME '14:00'),
  -- Maadi (Nile Central, Main Campus)
  (4, '25000000-0000-0000-0000-00000000e001'::uuid, 'b0000000-0000-4000-8000-000000000001'::uuid, 'b1000000-0000-4000-8000-000000000001'::uuid, '0190b100-0000-7000-8000-000000000004'::uuid, TIME '09:00', TIME '13:00'),
  (5, '25000000-0000-0000-0000-00000000e003'::uuid, 'b0000000-0000-4000-8000-000000000001'::uuid, 'b1000000-0000-4000-8000-000000000001'::uuid, '0190b100-0000-7000-8000-000000000004'::uuid, TIME '13:00', TIME '17:00'),
  -- Nasr City (Nile Central, East Annex) — Youssef's second clinic
  (6, '0190d0c0-0000-7000-8000-000000000002'::uuid, 'b0000000-0000-4000-8000-000000000001'::uuid, 'b1000000-0000-4000-8000-000000000002'::uuid, '0190b100-0000-7000-8000-000000000006'::uuid, TIME '14:00', TIME '17:00')
) AS d(seq, practitioner_id, provider_id, location_id, branch_id, starts, ends),
     unnest(ARRAY[0, 1, 2, 3, 4, 6]) AS dow
ON CONFLICT (availability_id) DO NOTHING;

COMMIT;

SELECT 'provider.branch' AS t, count(*) FROM provider.branch
UNION ALL SELECT 'provider.provider',                        count(*) FROM provider.provider
UNION ALL SELECT 'provider.provider_location',               count(*) FROM provider.provider_location
UNION ALL SELECT 'provider.network_tier',                    count(*) FROM provider.network_tier
UNION ALL SELECT 'provider.practitioner',                    count(*) FROM provider.practitioner
UNION ALL SELECT 'provider.practitioner_branch_assignment',  count(*) FROM provider.practitioner_branch_assignment
UNION ALL SELECT 'emr.provider_availability',                count(*) FROM emr.provider_availability
ORDER BY 1;
