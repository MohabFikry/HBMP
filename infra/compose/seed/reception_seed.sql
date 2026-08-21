-- Dev seed: reception/eligibility live data (synthetic, NOT real PHI).
-- Feeds eligibility.member_projection + coverage_projection so /api/v1/reception/search returns cards.
-- Idempotent: re-runnable.

-- card_number is NOT member_no. The first is the number printed on the card a beneficiary carries
-- (patient-service); the second is policy-service's enrolment key. Both must find the member, because the
-- desk is handed the card — see eligibility migration 0007.
INSERT INTO eligibility.member_projection
  (beneficiary_id, member_no, card_number, given_name, family_name, status, primary_phone, national_id, passport, refugee_id, unhcr_no, updated_at)
VALUES
  ('a1000000-0000-4000-8000-000000000001', 'MRS-M-100001', 'MRS-CARD-100001', 'Amina',   'Hassan',  'Active',    '01000000001', '29001010100011', 'P1234561', 'REF-100001', 'UNHCR-100001', now()),
  ('a1000000-0000-4000-8000-000000000002', 'MRS-M-100002', 'MRS-CARD-100002', 'Omar',    'Khalil',  'Active',    '01000000002', '29102020200022', 'P1234562', 'REF-100002', 'UNHCR-100002', now()),
  ('a1000000-0000-4000-8000-000000000003', 'MRS-M-100003', 'MRS-CARD-100003', 'Layla',   'Ahmed',   'Suspended', '01000000003', '29203030300033', 'P1234563', 'REF-100003', 'UNHCR-100003', now()),
  ('a1000000-0000-4000-8000-000000000004', 'MRS-M-100004', 'MRS-CARD-100004', 'Youssef', 'Ibrahim', 'Active',    '01000000004', '29304040400044', 'P1234564', 'REF-100004', 'UNHCR-100004', now())
ON CONFLICT (beneficiary_id) DO UPDATE SET
  member_no = EXCLUDED.member_no, card_number = EXCLUDED.card_number,
  given_name = EXCLUDED.given_name, family_name = EXCLUDED.family_name,
  status = EXCLUDED.status, primary_phone = EXCLUDED.primary_phone, national_id = EXCLUDED.national_id,
  passport = EXCLUDED.passport, refugee_id = EXCLUDED.refugee_id, unhcr_no = EXCLUDED.unhcr_no, updated_at = now();

-- benefit_category holds the canonical CODE (22 §11), never a display name: it is matched, not shown.
INSERT INTO eligibility.coverage_projection
  (coverage_id, beneficiary_id, benefit_category, policy_no, status, effective_from, effective_to, limits_json, updated_at)
VALUES
  ('c1000000-0000-4000-8000-000000000001', 'a1000000-0000-4000-8000-000000000001', 'CONSULT',    'POL-2026-0001', 'Active', DATE '2026-01-01', DATE '2026-12-31',
     '[{"limitType":"AnnualAmount","limitValue":50000,"consumedValue":12400}]'::jsonb, now()),
  ('c1000000-0000-4000-8000-000000000002', 'a1000000-0000-4000-8000-000000000001', 'PHARMACY',   'POL-2026-0001', 'Active', DATE '2026-01-01', DATE '2026-12-31',
     '[{"limitType":"AnnualAmount","limitValue":15000,"consumedValue":3200}]'::jsonb, now()),
  ('c1000000-0000-4000-8000-000000000003', 'a1000000-0000-4000-8000-000000000002', 'CONSULT',    'POL-2026-0002', 'Active', DATE '2026-01-01', DATE '2026-12-31',
     '[{"limitType":"AnnualAmount","limitValue":50000,"consumedValue":0}]'::jsonb, now()),
  ('c1000000-0000-4000-8000-000000000004', 'a1000000-0000-4000-8000-000000000002', 'LAB',        'POL-2026-0002', 'Active', DATE '2026-01-01', DATE '2026-12-31',
     '[{"limitType":"VisitCount","limitValue":20,"consumedValue":4}]'::jsonb, now()),
  ('c1000000-0000-4000-8000-000000000005', 'a1000000-0000-4000-8000-000000000003', 'CONSULT',    'POL-2026-0003', 'Suspended', DATE '2026-01-01', DATE '2026-12-31',
     '[{"limitType":"AnnualAmount","limitValue":50000,"consumedValue":48000}]'::jsonb, now()),
  ('c1000000-0000-4000-8000-000000000006', 'a1000000-0000-4000-8000-000000000004', 'CONSULT',    'POL-2026-0004', 'Active', DATE '2026-01-01', DATE '2026-12-31',
     '[{"limitType":"AnnualAmount","limitValue":50000,"consumedValue":9800}]'::jsonb, now())
ON CONFLICT (coverage_id) DO UPDATE SET
  benefit_category = EXCLUDED.benefit_category, policy_no = EXCLUDED.policy_no, status = EXCLUDED.status,
  effective_from = EXCLUDED.effective_from, effective_to = EXCLUDED.effective_to,
  limits_json = EXCLUDED.limits_json, updated_at = now();
