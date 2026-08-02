-- ============================================================================================================
-- seed-dev-clinic.sql — a coherent, realistic clinic operation for local testing.
--
--   psql -h localhost -p 55432 -U hbmp -d hbmp -v ON_ERROR_STOP=1 -f tools/dev/seed-dev-clinic.sql
--
-- Run AFTER tools/dev/reset-dev-data.sql. Idempotent by construction: every id is derived deterministically
-- from a fixed namespace, so re-running replaces the same rows instead of duplicating them.
--
-- SYNTHETIC, NOT REAL. Every person here is invented. The identifiers are format-valid so the services accept
-- them and search finds them, and format-valid is the ONLY thing they have in common with a real record — the
-- national IDs are arithmetically consistent but issued to nobody. CLAUDE.md: never real PHI in lower envs.
--
-- WHAT MAKES IT "COHERENT" — the property worth protecting when editing this file. Everything references
-- something that exists: every beneficiary has an enrolment, which points at a policy plan, which points at a
-- plan version that is effective today; every appointment sits in a slot that a doctor is actually rostered
-- for, in a branch that doctor is assigned to. Data that contradicts itself is worse than no data, because the
-- screens render it and you debug the fixture instead of the code.
--
-- THE PROJECTIONS ARE SEEDED TOO, deliberately. eligibility.member_projection is what reception AND the call
-- centre search — it is normally fed by events from patient-service, and with the outbox not replayed here it
-- would stay empty, so search would find nobody while the members plainly exist. Seeding both sides is the
-- honest way to get a working environment; it also means a change to patient.beneficiary here must be made in
-- the projection too, or search and the file will disagree.
-- ============================================================================================================

\set ON_ERROR_STOP on

SET app.tenant_id = '11111111-1111-1111-1111-111111111111';

BEGIN;

-- ── Deterministic ids ───────────────────────────────────────────────────────────────────────────────────────
-- uuid_generate_v5-style derivation without the extension: md5 of a namespaced label, shaped into a v4-looking
-- uuid. Stable across runs, so re-seeding updates rows rather than creating a second set — and so the ids in
-- this file can be quoted in a bug report and still mean the same row tomorrow.
CREATE OR REPLACE FUNCTION pg_temp.did(label text) RETURNS uuid LANGUAGE sql IMMUTABLE AS $$
    SELECT (substr(m,1,8)||'-'||substr(m,9,4)||'-4'||substr(m,14,3)||'-a'||substr(m,18,3)||'-'||substr(m,21,12))::uuid
    FROM (SELECT md5('mersal-dev-seed:'||label) AS m) s;
$$;

-- ── The organisation is NOT seeded here ─────────────────────────────────────────────────────────────────────
-- Branches, providers, network tiers, practitioners and clinic rosters live in
-- tools/dev/restore-reference-structure.sql and are the REAL ones — six branches with the codes already in use
-- (DOK, NSR, MAA, ALX, OCT, ASW), three providers, six named practitioners. An earlier pass at this file
-- invented its own ("Mersal Dokki", BR-DOK, md5-derived uuids) and discarded them; structure is not test
-- payload, and every screenshot, saved URL and frontend fixture refers to those ids.
--
-- Run that file first. This one fails loudly below if it has not been.
DO $need_structure$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM provider.branch WHERE branch_code = 'DOK')
       OR NOT EXISTS (SELECT 1 FROM provider.practitioner WHERE user_id = 'demo-dr-hana') THEN
        RAISE EXCEPTION
            'the branches and practitioners are missing — run tools/dev/restore-reference-structure.sql first';
    END IF;
END
$need_structure$;

-- ── Payer, plans, policy ────────────────────────────────────────────────────────────────────────────────────
-- `SelfFunded`, not "Charity": payer_type is a closed set (SelfFunded / Donor / Government / PartnerNGO /
-- Insurer) and the foundation funds its beneficiaries' care from its own resources rather than passing a bill
-- to an insurer. Donor would describe money coming IN, which is a different relationship.
INSERT INTO policy.payer (payer_id, tenant_id, payer_code, name_en, name_ar, payer_type, status)
VALUES (pg_temp.did('payer:mersal'), '11111111-1111-1111-1111-111111111111', 'PAY-MRS',
        'Mersal Foundation', 'مؤسسة مرسال', 'SelfFunded', 'Active')
ON CONFLICT (payer_id) DO UPDATE SET name_en = EXCLUDED.name_en;

INSERT INTO policy.plan (plan_id, tenant_id, plan_code, name_en, name_ar, category, status)
VALUES
  (pg_temp.did('plan:std'),  '11111111-1111-1111-1111-111111111111', 'PLN-STD',  'Standard Care', 'الرعاية الأساسية', 'Outpatient', 'Active'),
  (pg_temp.did('plan:fam'),  '11111111-1111-1111-1111-111111111111', 'PLN-FAM',  'Family Care',   'رعاية الأسرة',     'Outpatient', 'Active')
ON CONFLICT (plan_id) DO UPDATE SET name_en = EXCLUDED.name_en;

INSERT INTO policy.plan_version (plan_version_id, tenant_id, plan_id, version_no, effective_from, status, activated_at)
VALUES
  (pg_temp.did('pv:std'), '11111111-1111-1111-1111-111111111111', pg_temp.did('plan:std'), 1, CURRENT_DATE - 365, 'Active', now()),
  (pg_temp.did('pv:fam'), '11111111-1111-1111-1111-111111111111', pg_temp.did('plan:fam'), 1, CURRENT_DATE - 365, 'Active', now())
ON CONFLICT (plan_version_id) DO UPDATE SET status = 'Active';

INSERT INTO policy.policy (policy_id, policy_no, sponsor, effective_from, effective_to, status, tenant_id, payer_id, max_members)
VALUES (pg_temp.did('policy:2026'), 'POL-2026-0001', 'Mersal Foundation', CURRENT_DATE - 200, CURRENT_DATE + 165,
        'Active', '11111111-1111-1111-1111-111111111111', pg_temp.did('payer:mersal'), 500)
ON CONFLICT (policy_id) DO UPDATE SET status = 'Active';

INSERT INTO policy.policy_plan (policy_plan_id, tenant_id, policy_id, plan_version_id, plan_label, effective_from, is_default, status)
VALUES
  (pg_temp.did('pp:std'), '11111111-1111-1111-1111-111111111111', pg_temp.did('policy:2026'), pg_temp.did('pv:std'), 'Standard Care', CURRENT_DATE - 200, true,  'Active'),
  (pg_temp.did('pp:fam'), '11111111-1111-1111-1111-111111111111', pg_temp.did('policy:2026'), pg_temp.did('pv:fam'), 'Family Care',   CURRENT_DATE - 200, false, 'Active')
ON CONFLICT (policy_plan_id) DO UPDATE SET status = 'Active';

-- ── 25 beneficiaries ────────────────────────────────────────────────────────────────────────────────────────
-- Egyptian, Sudanese and Syrian names — the cohort Mersal actually serves — with every value written out per
-- person rather than computed. An earlier pass generated phones as `'01' || (n % 3) || (10000000 + n*137)` and
-- national IDs from the row number, which produces strings that are the right LENGTH and unmistakably fake:
-- consecutive members with consecutive numbers, every phone on the same three prefixes. You cannot judge a
-- search result list, or spot a formatting bug, against data that no real record resembles.
--
-- National IDs follow the real Egyptian structure: century digit (2 = 1900s, 3 = 2000s), YYMMDD, two-digit
-- governorate (01 Cairo, 02 Alexandria, 21 Giza, 28 Aswan, 88 born abroad), a four-digit serial whose last
-- digit is odd for men and even for women, then a check digit. They are structurally valid and issued to
-- nobody. Non-Egyptians carry a UNHCR number instead, which is what they actually present at the desk.
--
-- Mobile numbers span all four Egyptian networks — 010 Vodafone, 011 Etisalat, 012 Orange, 015 WE.
--
-- Statuses are mixed on purpose: an environment where every member is Active never exercises "Suspended —
-- cannot be booked", and that is the path that fails quietly.
CREATE TEMP TABLE seed_people (
    n int, given_en text, family_en text, given_ar text, family_ar text,
    sex text, born date, nationality text, status text,
    national_id text, unhcr_no text, phone text, card_no text
) ON COMMIT DROP;

INSERT INTO seed_people VALUES
  ( 1,'Amal','Hassan','أمل','حسن','Female','1989-03-14','EG','Active','28903140102846','','01001234567','MRS-2026-0001'),
  ( 2,'Hana','Mansour','هناء','منصور','Female','1994-07-02','EG','Active','29407022101624','','01223456789','MRS-2026-0002'),
  ( 3,'Omar','Khalil','عمر','خليل','Male','1981-11-23','EG','Active','28111230101335','','01098765432','MRS-2026-0003'),
  ( 4,'Fatma','Ibrahim','فاطمة','إبراهيم','Female','1976-01-30','EG','Active','27601300204482','','01112233445','MRS-2026-0004'),
  ( 5,'Youssef','Adel','يوسف','عادل','Male','2001-05-19','EG','Active','30105190102173','','01555667788','MRS-2026-0005'),
  ( 6,'Mariam','Saleh','مريم','صالح','Female','1998-09-08','EG','Active','29809082103268','','01276543210','MRS-2026-0006'),
  ( 7,'Ahmed','Farouk','أحمد','فاروق','Male','1967-04-11','EG','Active','26704110101917','','01019283746','MRS-2026-0007'),
  ( 8,'Nour','El-Sayed','نور','السيد','Female','2015-12-01','EG','Active','31512010102604','','01144556677','MRS-2026-0008'),
  ( 9,'Khaled','Mostafa','خالد','مصطفى','Male','1990-08-27','EG','Active','29008270203551','','01566778899','MRS-2026-0009'),
  (10,'Salma','Gamal','سلمى','جمال','Female','1985-02-17','EG','Active','28502170101428','','01234567890','MRS-2026-0010'),
  (11,'Mohamed','Ali','محمد','علي','Male','1972-06-06','EG','Active','27206060102739','','01087654321','MRS-2026-0011'),
  (12,'Rania','Zaki','رانيا','زكي','Female','1996-10-25','EG','Active','29610252801866','','01198765432','MRS-2026-0012'),
  (13,'Tarek','Selim','طارق','سليم','Male','1988-03-03','EG','Active','28803030101195','','01023456781','MRS-2026-0013'),
  (14,'Dalia','Nabil','داليا','نبيل','Female','2003-07-14','EG','Active','30307140204042','','01287654321','MRS-2026-0014'),
  (15,'Hassan','Awad','حسن','عوض','Male','1959-11-09','EG','Active','25911090102313','','01011223344','MRS-2026-0015'),
  (16,'Yasmin','Sherif','ياسمين','شريف','Female','1992-01-21','EG','Active','29201210101682','','01155667788','MRS-2026-0016'),
  -- Sudanese and Syrian members: no Egyptian national ID, a UNHCR file number instead.
  (17,'Ibrahim','Deng','إبراهيم','دينق','Male','1979-05-30','SD','Active','','760-C01847392','01221334455','MRS-2026-0017'),
  (18,'Aisha','Nyandeng','عائشة','نيانديق','Female','1986-08-12','SD','Active','','760-C01926184','01003456712','MRS-2026-0018'),
  (19,'Layla','Haddad','ليلى','حداد','Female','1993-04-04','SY','Active','','760-C02073519','01119876543','MRS-2026-0019'),
  (20,'Bassam','Kanaan','بسام','كنعان','Male','1974-12-19','SY','Active','','760-C02158460','01566223344','MRS-2026-0020'),
  (21,'Mostafa','Younis','مصطفى','يونس','Male','2010-02-28','EG','Active','31002280101577','','01277889900','MRS-2026-0021'),
  -- Suspended: reception and the call centre must refuse to book these two.
  (22,'Heba','Kamal','هبة','كمال','Female','1983-09-16','EG','Suspended','28309160103924','','01044556677','MRS-2026-0022'),
  (23,'Sara','Lotfy','سارة','لطفي','Female','1997-06-23','EG','Suspended','29706232102786','','01233445566','MRS-2026-0023'),
  -- Coverage lapsed a fortnight ago.
  (24,'Adel','Mahmoud','عادل','محمود','Male','1965-10-07','EG','Expired','26510070101733','','01099887766','MRS-2026-0024'),
  -- Registered at the desk but not yet activated: no enrolment, no coverage.
  (25,'Nadia','Fouad','نادية','فؤاد','Female','2000-03-31','EG','Pending','30003310204168','','01188776655','MRS-2026-0025');

INSERT INTO patient.beneficiary
  (beneficiary_id, member_no, given_name, family_name, birth_date, sex, nationality_code, status,
   card_number, created_by, updated_by, tenant_id)
SELECT pg_temp.did('ben:'||p.n),
       'MRS-M-2026-'||lpad(p.n::text, 6, '0'),
       p.given_en, p.family_en, p.born, p.sex, p.nationality, p.status, p.card_no,
       'seed', 'seed', '11111111-1111-1111-1111-111111111111'
FROM seed_people p
ON CONFLICT (beneficiary_id) DO UPDATE
  SET given_name = EXCLUDED.given_name, family_name = EXCLUDED.family_name, status = EXCLUDED.status,
      sex = EXCLUDED.sex, nationality_code = EXCLUDED.nationality_code, card_number = EXCLUDED.card_number;

-- Identifiers, from the columns above rather than derived: the value on the card is the value in the record.
INSERT INTO patient.beneficiary_identifier
  (identifier_id, beneficiary_id, identifier_type, identifier_value, issuing_country, is_primary, tenant_id)
SELECT pg_temp.did('id:nat:'||p.n), pg_temp.did('ben:'||p.n), 'NationalID', p.national_id,
       'EG', true, '11111111-1111-1111-1111-111111111111'
FROM seed_people p WHERE p.national_id <> ''
ON CONFLICT (identifier_id) DO UPDATE SET identifier_value = EXCLUDED.identifier_value;

INSERT INTO patient.beneficiary_identifier
  (identifier_id, beneficiary_id, identifier_type, identifier_value, issuing_country, is_primary, tenant_id)
SELECT pg_temp.did('id:unhcr:'||p.n), pg_temp.did('ben:'||p.n), 'UNHCRNo', p.unhcr_no,
       p.nationality, true, '11111111-1111-1111-1111-111111111111'
FROM seed_people p WHERE p.unhcr_no <> ''
ON CONFLICT (identifier_id) DO UPDATE SET identifier_value = EXCLUDED.identifier_value;

-- The member number is an identifier in its own right — reception looks members up by it, and it is what is
-- printed on the card they hand over.
INSERT INTO patient.beneficiary_identifier
  (identifier_id, beneficiary_id, identifier_type, identifier_value, issuing_country, is_primary, tenant_id)
SELECT pg_temp.did('id:mem:'||p.n), pg_temp.did('ben:'||p.n), 'MemberNo',
       'MRS-M-2026-'||lpad(p.n::text, 6, '0'), 'EG', false, '11111111-1111-1111-1111-111111111111'
FROM seed_people p
ON CONFLICT (identifier_id) DO UPDATE SET identifier_value = EXCLUDED.identifier_value;

-- Contacts. WhatsApp is the realistic default channel for this cohort; a few members are email-reachable too.
INSERT INTO patient.contact (contact_id, beneficiary_id, contact_type, value, preferred_channel, is_primary, tenant_id)
SELECT pg_temp.did('ct:ph:'||p.n), pg_temp.did('ben:'||p.n), 'Phone', p.phone,
       'WhatsApp', true, '11111111-1111-1111-1111-111111111111'
FROM seed_people p
ON CONFLICT (contact_id) DO UPDATE SET value = EXCLUDED.value;

INSERT INTO patient.contact (contact_id, beneficiary_id, contact_type, value, preferred_channel, is_primary, tenant_id)
SELECT pg_temp.did('ct:em:'||p.n), pg_temp.did('ben:'||p.n), 'Email',
       lower(p.given_en)||'.'||lower(replace(p.family_en,'-',''))||'@example.test',
       'Email', false, '11111111-1111-1111-1111-111111111111'
FROM seed_people p WHERE p.n IN (2, 5, 10, 13, 16, 19, 21)
ON CONFLICT (contact_id) DO UPDATE SET value = EXCLUDED.value;

-- ── Enrolments, coverage and limits ─────────────────────────────────────────────────────────────────────────
-- Everyone but the Pending member is enrolled. Children go on Family Care, adults on Standard.
-- `termination_reason` is set IN THE INSERT, not fixed up afterwards: ck_enrollment_termination_reason is a
-- row check, so it fires the moment a Terminated row is written and a follow-up UPDATE never gets the chance.
-- The constraint is right — a termination with no stated reason is a record that cannot be explained later.
INSERT INTO policy.enrollment
  (enrollment_id, tenant_id, beneficiary_id, policy_id, policy_plan_id, member_no, relationship,
   effective_from, status, termination_reason, source_plan_version_id, network_tier_id)
SELECT pg_temp.did('enr:'||p.n), '11111111-1111-1111-1111-111111111111', pg_temp.did('ben:'||p.n),
       pg_temp.did('policy:2026'),
       CASE WHEN age(p.born) < interval '18 years' THEN pg_temp.did('pp:fam') ELSE pg_temp.did('pp:std') END,
       'MRS-M-2026-'||lpad(p.n::text, 6, '0'), 'Principal',
       CURRENT_DATE - 200,
       CASE p.status WHEN 'Active' THEN 'Active' WHEN 'Suspended' THEN 'Suspended' ELSE 'Terminated' END,
       CASE WHEN p.status NOT IN ('Active', 'Suspended') THEN 'Coverage period ended' END,
       CASE WHEN age(p.born) < interval '18 years' THEN pg_temp.did('pv:fam') ELSE pg_temp.did('pv:std') END,
       -- The real Tier 1 contracted network, from restore-reference-structure.sql.
       'f1c08cbb-38ad-4dad-89e0-22124dc4a89b'::uuid
FROM seed_people p WHERE p.status <> 'Pending'
ON CONFLICT (enrollment_id) DO UPDATE
  SET status = EXCLUDED.status, termination_reason = EXCLUDED.termination_reason;

INSERT INTO policy.coverage
  (coverage_id, policy_id, beneficiary_id, benefit_category_id, effective_from, effective_to, status,
   tenant_id, source_plan_version_id, enrollment_id)
SELECT pg_temp.did('cov:'||p.n||':'||bc.code), pg_temp.did('policy:2026'), pg_temp.did('ben:'||p.n),
       bc.benefit_category_id, CURRENT_DATE - 200,
       CASE WHEN p.status = 'Expired' THEN CURRENT_DATE - 10 ELSE CURRENT_DATE + 165 END,
       CASE p.status WHEN 'Active' THEN 'Active' WHEN 'Suspended' THEN 'Suspended' ELSE 'Expired' END,
       '11111111-1111-1111-1111-111111111111',
       CASE WHEN age(p.born) < interval '18 years' THEN pg_temp.did('pv:fam') ELSE pg_temp.did('pv:std') END,
       pg_temp.did('enr:'||p.n)
FROM seed_people p
CROSS JOIN policy.benefit_category bc
WHERE p.status <> 'Pending' AND bc.code IN ('CONSULT', 'LAB', 'PHARMACY', 'IMAGING')
ON CONFLICT (coverage_id) DO UPDATE SET status = EXCLUDED.status;

-- Annual limits with realistic partial consumption, so "remaining" is a number worth reading rather than
-- always the full allowance.
INSERT INTO policy.coverage_limit
  (coverage_limit_id, coverage_id, limit_type, limit_value, consumed_value, currency_code, reset_period, tenant_id)
SELECT pg_temp.did('lim:'||p.n||':'||bc.code), pg_temp.did('cov:'||p.n||':'||bc.code), 'Annual',
       CASE bc.code WHEN 'CONSULT' THEN 6000 WHEN 'LAB' THEN 4000 WHEN 'PHARMACY' THEN 5000 ELSE 8000 END,
       -- Deterministic pseudo-consumption: varied per member and category, never above the limit.
       ((p.n * 173 + length(bc.code) * 91) % 60)::numeric / 100
         * CASE bc.code WHEN 'CONSULT' THEN 6000 WHEN 'LAB' THEN 4000 WHEN 'PHARMACY' THEN 5000 ELSE 8000 END,
       'EGP', 'Yearly', '11111111-1111-1111-1111-111111111111'
FROM seed_people p
CROSS JOIN policy.benefit_category bc
WHERE p.status <> 'Pending' AND bc.code IN ('CONSULT', 'LAB', 'PHARMACY', 'IMAGING')
ON CONFLICT (coverage_limit_id) DO UPDATE SET consumed_value = EXCLUDED.consumed_value;

-- ── Eligibility projections — what reception and the call centre actually SEARCH ────────────────────────────
INSERT INTO eligibility.member_projection
  (beneficiary_id, member_no, given_name, family_name, status, primary_phone, national_id, unhcr_no, tenant_id)
SELECT b.beneficiary_id, b.member_no, b.given_name, b.family_name, b.status,
       (SELECT c.value FROM patient.contact c
         WHERE c.beneficiary_id = b.beneficiary_id AND c.contact_type = 'Phone' AND c.is_primary LIMIT 1),
       (SELECT i.identifier_value FROM patient.beneficiary_identifier i
         WHERE i.beneficiary_id = b.beneficiary_id AND i.identifier_type = 'NationalID' LIMIT 1),
       (SELECT i.identifier_value FROM patient.beneficiary_identifier i
         WHERE i.beneficiary_id = b.beneficiary_id AND i.identifier_type = 'UNHCRNo' LIMIT 1),
       b.tenant_id
FROM patient.beneficiary b
ON CONFLICT (beneficiary_id) DO UPDATE
  SET member_no = EXCLUDED.member_no, given_name = EXCLUDED.given_name, family_name = EXCLUDED.family_name,
      status = EXCLUDED.status, primary_phone = EXCLUDED.primary_phone, national_id = EXCLUDED.national_id,
      unhcr_no = EXCLUDED.unhcr_no, updated_at = now();

INSERT INTO eligibility.coverage_projection
  (coverage_id, beneficiary_id, benefit_category, policy_no, status, effective_from, effective_to, limits_json, tenant_id)
SELECT c.coverage_id, c.beneficiary_id, bc.name, 'POL-2026-0001', c.status, c.effective_from, c.effective_to,
       jsonb_build_array(jsonb_build_object(
         'limitType', cl.limit_type, 'limitValue', cl.limit_value, 'consumedValue', cl.consumed_value)),
       c.tenant_id
FROM policy.coverage c
JOIN policy.benefit_category bc ON bc.benefit_category_id = c.benefit_category_id
JOIN policy.coverage_limit cl   ON cl.coverage_id = c.coverage_id
ON CONFLICT (coverage_id) DO UPDATE
  SET status = EXCLUDED.status, limits_json = EXCLUDED.limits_json, updated_at = now();

-- ── Slots: the past week AND the next, from each doctor's roster ───────────────────────────────────────────
-- Backwards as well as forwards, because appointments in the past are what give the environment a HISTORY —
-- completed visits, a no-show, someone checked in this morning. Generating only from today produced a board on
-- which every appointment was still Booked, so nothing downstream (visit history, no-show reports, the
-- profile's timeline) had anything to show.
INSERT INTO emr.appointment_slot (slot_id, provider_id, location_id, doctor_id, slot_start, slot_end, branch_id, tenant_id)
SELECT pg_temp.did('slot:'||a.availability_id||':'||d.day||':'||t.mins),
       a.provider_id, a.location_id, a.doctor_id,
       (d.day + a.start_time) AT TIME ZONE 'Africa/Cairo' + (t.mins || ' minutes')::interval,
       (d.day + a.start_time) AT TIME ZONE 'Africa/Cairo' + ((t.mins + a.slot_minutes) || ' minutes')::interval,
       a.branch_id, a.tenant_id
FROM emr.provider_availability a
CROSS JOIN LATERAL (
    SELECT (CURRENT_DATE + offs)::date AS day
    FROM generate_series(-7, 7) AS offs
) d
CROSS JOIN LATERAL (
    SELECT gs AS mins
    FROM generate_series(0, (extract(epoch FROM (a.end_time - a.start_time)) / 60)::int - a.slot_minutes, a.slot_minutes) gs
) t
-- Only days the doctor is actually rostered for. Slots on a day nobody works are the classic way a booking
-- screen offers a time the server then refuses.
WHERE extract(dow FROM d.day) = a.day_of_week
ON CONFLICT (slot_id) DO NOTHING;

-- ── Appointments: a believable board, in mixed states ───────────────────────────────────────────────────────
-- Taken from real slots so every appointment sits in a time its doctor is rostered for. Past days are
-- Completed or NoShow, today and ahead are Booked or CheckedIn, plus a couple of Cancelled.
-- Planned first, written second: these rows are inserted as Booked and then WALKED to the state they belong
-- in, because `emr.appointment_history` is filled by a row trigger and a row inserted straight into its final
-- state has a one-step history. The timeline then opens at "Checked in" with no booking above it — a visit
-- that begins mid-story, and no answer to "when was this arranged?".
CREATE TEMP TABLE appt_plan ON COMMIT DROP AS
SELECT pg_temp.did('appt:'||s.slot_id) AS appointment_id,
       b.beneficiary_id, s.provider_id, s.location_id, s.slot_id,
       CASE
         WHEN s.slot_start < now() - interval '1 day' THEN (CASE WHEN s.rn % 7 = 0 THEN 'NoShow' ELSE 'Completed' END)
         WHEN s.rn % 11 = 0 THEN 'Cancelled'
         WHEN s.slot_start < now() THEN 'CheckedIn'
         ELSE 'Booked'
       END AS appt_status,
       s.slot_start, s.slot_end, s.branch_id, s.tenant_id, s.doctor_id,
       b.given_name || ' ' || b.family_name AS beneficiary_name
FROM (
    SELECT s.*, row_number() OVER (ORDER BY s.slot_start, s.slot_id) AS rn
    FROM emr.appointment_slot s
    -- Every 23rd slot, so the board is populated but far from full — a clinic with no free times cannot be
    -- used to test booking, which is the thing this environment exists for.
    WHERE (('x' || substr(md5(s.slot_id::text), 1, 8))::bit(32)::bigint % 23) = 0
) s
JOIN LATERAL (
    SELECT bn.* FROM patient.beneficiary bn
    WHERE bn.status = 'Active'
    ORDER BY md5(bn.beneficiary_id::text || s.slot_id::text)
    LIMIT 1
) b ON true;

INSERT INTO emr.appointment
  (appointment_id, beneficiary_id, provider_id, location_id, slot_id, appointment_type, status,
   scheduled_start, scheduled_end, branch_id, tenant_id, doctor_id, beneficiary_name, created_by, created_at)
SELECT p.appointment_id, p.beneficiary_id, p.provider_id, p.location_id, p.slot_id, 'Scheduled', 'Booked',
       p.slot_start, p.slot_end, p.branch_id, p.tenant_id, p.doctor_id, p.beneficiary_name, 'seed', now()
FROM appt_plan p
-- Untargeted: the appointment id is derived from the slot, so a re-run collides on the primary key — but a
-- slot may also already hold an appointment written by another seed under a different id, and now that every
-- row starts Booked that meets `ux_appointment_active_slot` too. A seed skips; it does not abort a database.
ON CONFLICT DO NOTHING;

-- The walk. Guarded on the CURRENT status so a re-run is a no-op, and so a NoShow goes Booked → NoShow
-- without passing through CheckedIn: not arriving is the entire content of a no-show.
--
-- That guard also means this only reshapes appointments this run CREATES. Rows seeded before it existed keep
-- their one-step history until the database is seeded from scratch — deliberately, because the alternative is
-- resetting live rows to Booked, and an appointment seeded as Booked and since checked in through the portal
-- is indistinguishable here from one that was never touched. A seed does not un-check-in a patient.
UPDATE emr.appointment a SET status = 'CheckedIn', updated_at = now()
  FROM appt_plan p
 WHERE a.appointment_id = p.appointment_id AND a.status = 'Booked'
   AND p.appt_status IN ('CheckedIn', 'Completed');

UPDATE emr.appointment a SET status = 'Completed', updated_at = now()
  FROM appt_plan p
 WHERE a.appointment_id = p.appointment_id AND a.status = 'CheckedIn' AND p.appt_status = 'Completed';

UPDATE emr.appointment a SET status = p.appt_status, no_show = (p.appt_status = 'NoShow'),
       cancel_reason = CASE WHEN p.appt_status = 'Cancelled' THEN 'PatientRequest' END, updated_at = now()
  FROM appt_plan p
 WHERE a.appointment_id = p.appointment_id AND a.status = 'Booked'
   AND p.appt_status IN ('NoShow', 'Cancelled');

-- The trigger stamps every one of those steps with the transaction clock, which would file the booking, the
-- arrival and the finished visit at the same second. Invented appointments get invented history, held to the
-- same standard: a booking always in the past, arrival just before the slot, the visit ending within it.
UPDATE emr.appointment_history h
   SET changed_at = CASE h.row_snapshot ->> 'status'
         WHEN 'Booked'    THEN LEAST(now() - interval '2 days', a.scheduled_start - interval '3 days')
         WHEN 'CheckedIn' THEN a.scheduled_start - interval '9 minutes'
         WHEN 'Completed' THEN a.scheduled_start + interval '26 minutes'
         WHEN 'NoShow'    THEN a.scheduled_start + interval '20 minutes'
         WHEN 'Cancelled' THEN LEAST(now() - interval '1 hour', a.scheduled_start - interval '1 day')
         ELSE h.changed_at
       END
  FROM emr.appointment a
 WHERE a.appointment_id = h.appointment_id AND a.created_by = 'seed';

-- ── Call-centre history ─────────────────────────────────────────────────────────────────────────────────────
-- Closed calls with summaries, because the summary is what other roles read on the patient profile — an empty
-- call history makes that whole section untestable. Off-system attestations, matching how identity is
-- confirmed now (the agent verifies on the phone; the platform records that they did).
INSERT INTO callcentre.call_seq (year, last_value) VALUES (2026, 10)
ON CONFLICT (year) DO UPDATE SET last_value = GREATEST(callcentre.call_seq.last_value, 10);

INSERT INTO callcentre.call_interaction
  (interaction_id, call_ref, tenant_id, beneficiary_id, agent_user_id, direction, started_at, ended_at,
   reason_code, outcome, summary, status, created_by, created_at, updated_at)
SELECT pg_temp.did('call:'||p.n), 'CALL-2026-'||lpad(p.n::text, 6, '0'),
       '11111111-1111-1111-1111-111111111111', pg_temp.did('ben:'||p.n),
       pg_temp.did('agent:1'),
       CASE WHEN p.n % 4 = 0 THEN 'Outbound' ELSE 'Inbound' END,
       now() - ((p.n * 6) || ' hours')::interval,
       now() - ((p.n * 6) || ' hours')::interval + interval '7 minutes',
       (ARRAY['BookAppointment','RescheduleAppointment','AppointmentEnquiry','EligibilityEnquiry','UpdateContact'])[1 + (p.n % 5)],
       'Resolved',
       (ARRAY[
         'Booked a follow-up consultation at Dokki and confirmed the time with the member.',
         'Moved the appointment to the following week at the member''s request.',
         'Confirmed the date and branch of the upcoming appointment.',
         'Explained the remaining outpatient limit for this year.',
         'Corrected the primary phone number on the member''s record.'
       ])[1 + (p.n % 5)],
       'Closed', 'seed', now() - ((p.n * 6) || ' hours')::interval, now()
FROM seed_people p WHERE p.n <= 10
ON CONFLICT (interaction_id) DO UPDATE SET summary = EXCLUDED.summary;

INSERT INTO callcentre.caller_verification
  (verification_id, interaction_id, beneficiary_id, tenant_id, verified_identifiers, result, method, verified_at, verified_by)
SELECT pg_temp.did('ver:'||p.n), pg_temp.did('call:'||p.n), pg_temp.did('ben:'||p.n),
       '11111111-1111-1111-1111-111111111111', '[]'::jsonb, 'Passed', 'OffSystem',
       now() - ((p.n * 6) || ' hours')::interval, 'seed'
FROM seed_people p WHERE p.n <= 10
ON CONFLICT (verification_id) DO UPDATE SET method = 'OffSystem';

COMMIT;

-- ── What was seeded ─────────────────────────────────────────────────────────────────────────────────────────
SELECT 'provider.branch' AS t, count(*) FROM provider.branch
UNION ALL SELECT 'provider.practitioner',           count(*) FROM provider.practitioner
UNION ALL SELECT 'patient.beneficiary',             count(*) FROM patient.beneficiary
UNION ALL SELECT 'patient.contact',                 count(*) FROM patient.contact
UNION ALL SELECT 'policy.enrollment',               count(*) FROM policy.enrollment
UNION ALL SELECT 'policy.coverage',                 count(*) FROM policy.coverage
UNION ALL SELECT 'policy.coverage_limit',           count(*) FROM policy.coverage_limit
UNION ALL SELECT 'eligibility.member_projection',   count(*) FROM eligibility.member_projection
UNION ALL SELECT 'eligibility.coverage_projection', count(*) FROM eligibility.coverage_projection
UNION ALL SELECT 'emr.appointment_slot',            count(*) FROM emr.appointment_slot
UNION ALL SELECT 'emr.appointment',                 count(*) FROM emr.appointment
UNION ALL SELECT 'callcentre.call_interaction',     count(*) FROM callcentre.call_interaction
ORDER BY 1;
