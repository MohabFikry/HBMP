-- Dev seed: executive dashboard read-model facts (synthetic, aggregate, PHI-free). Populates the reporting
-- fact tables for tenant 11111111… so /dashboards/executive renders live widgets. Idempotent: re-runnable.

\set tenant '''11111111-1111-1111-1111-111111111111'''

-- Approval TAT trend + rejected-request breakdown (authorization_fact)
INSERT INTO reporting.authorization_fact (fact_id, event_id, tenant_id, auth_no, priority, outcome, reviewer_id, rejection_reason_code, tat_seconds, sla_breached, period, decided_at) VALUES
  ('f0000001-0000-4000-8000-000000000001','e0000001-0000-4000-8000-000000000001',:tenant,'AUTH-9001','Routine',  'Approved', 'r1', NULL,               3600, false, CURRENT_DATE - 3, now() - interval '3 day'),
  ('f0000001-0000-4000-8000-000000000002','e0000001-0000-4000-8000-000000000002',:tenant,'AUTH-9002','Routine',  'Approved', 'r1', NULL,               5400, false, CURRENT_DATE - 2, now() - interval '2 day'),
  ('f0000001-0000-4000-8000-000000000003','e0000001-0000-4000-8000-000000000003',:tenant,'AUTH-9003','Urgent',   'Approved', 'r2', NULL,               1800, false, CURRENT_DATE - 2, now() - interval '2 day'),
  ('f0000001-0000-4000-8000-000000000004','e0000001-0000-4000-8000-000000000004',:tenant,'AUTH-9004','Urgent',   'Rejected', 'r2', 'NOT_COVERED',      2400, false, CURRENT_DATE - 1, now() - interval '1 day'),
  ('f0000001-0000-4000-8000-000000000005','e0000001-0000-4000-8000-000000000005',:tenant,'AUTH-9005','Emergency','Approved', 'r1', NULL,                600, false, CURRENT_DATE - 1, now() - interval '1 day'),
  ('f0000001-0000-4000-8000-000000000006','e0000001-0000-4000-8000-000000000006',:tenant,'AUTH-9006','Routine',  'Rejected', 'r3', 'INSUFFICIENT_DOCS',7200, true,  CURRENT_DATE,     now())
ON CONFLICT (fact_id) DO NOTHING;

-- Pending approvals gauge (pending_authorization)
INSERT INTO reporting.pending_authorization (authorization_id, tenant_id, priority, status, submitted_at, sla_due_at, sla_breached) VALUES
  ('f1000001-0000-4000-8000-000000000001',:tenant,'Emergency','Submitted',  now() - interval '5 hour', now() - interval '1 hour', true),
  ('f1000001-0000-4000-8000-000000000002',:tenant,'Urgent',   'Submitted',  now() - interval '2 hour', now() + interval '6 hour', false),
  ('f1000001-0000-4000-8000-000000000003',:tenant,'Routine',  'UnderReview',now() - interval '3 hour', now() + interval '46 hour',false)
ON CONFLICT (authorization_id) DO NOTHING;

-- Clinic workload bars + no-show trend (encounter_fact)
INSERT INTO reporting.encounter_fact (fact_id, event_id, tenant_id, clinic_id, kind, period, count) VALUES
  ('f2000001-0000-4000-8000-000000000001','e2000001-0000-4000-8000-000000000001',:tenant,'General Clinic',    'Encounter', CURRENT_DATE - 2, 14),
  ('f2000001-0000-4000-8000-000000000002','e2000001-0000-4000-8000-000000000002',:tenant,'General Clinic',    'Encounter', CURRENT_DATE - 1, 18),
  ('f2000001-0000-4000-8000-000000000003','e2000001-0000-4000-8000-000000000003',:tenant,'Pediatrics Clinic', 'Encounter', CURRENT_DATE - 1, 9),
  ('f2000001-0000-4000-8000-000000000010','e2000001-0000-4000-8000-000000000010',:tenant,'General Clinic',    'Booked',    CURRENT_DATE - 1, 40),
  ('f2000001-0000-4000-8000-000000000011','e2000001-0000-4000-8000-000000000011',:tenant,'General Clinic',    'Attended',  CURRENT_DATE - 1, 34),
  ('f2000001-0000-4000-8000-000000000012','e2000001-0000-4000-8000-000000000012',:tenant,'General Clinic',    'NoShow',    CURRENT_DATE - 1, 6),
  ('f2000001-0000-4000-8000-000000000013','e2000001-0000-4000-8000-000000000013',:tenant,'Pediatrics Clinic', 'Booked',    CURRENT_DATE - 1, 20),
  ('f2000001-0000-4000-8000-000000000014','e2000001-0000-4000-8000-000000000014',:tenant,'Pediatrics Clinic', 'Attended',  CURRENT_DATE - 1, 17),
  ('f2000001-0000-4000-8000-000000000015','e2000001-0000-4000-8000-000000000015',:tenant,'Pediatrics Clinic', 'NoShow',    CURRENT_DATE - 1, 3)
ON CONFLICT (fact_id) DO NOTHING;

-- Utilization by service line (utilization_fact, dimension=Provider)
INSERT INTO reporting.utilization_fact (fact_id, event_id, tenant_id, dimension, code, period, count) VALUES
  ('f3000001-0000-4000-8000-000000000001','e3000001-0000-4000-8000-000000000001',:tenant,'Provider','Laboratory', CURRENT_DATE - 1, 52),
  ('f3000001-0000-4000-8000-000000000002','e3000001-0000-4000-8000-000000000002',:tenant,'Provider','Imaging',    CURRENT_DATE - 1, 28),
  ('f3000001-0000-4000-8000-000000000003','e3000001-0000-4000-8000-000000000003',:tenant,'Provider','Pharmacy',   CURRENT_DATE - 1, 71),
  ('f3000001-0000-4000-8000-000000000004','e3000001-0000-4000-8000-000000000004',:tenant,'Provider','Consultation',CURRENT_DATE - 1, 96)
ON CONFLICT (fact_id) DO NOTHING;

-- Top diagnoses + medications (code_count)
INSERT INTO reporting.code_count (fact_id, event_id, tenant_id, kind, code, period, count) VALUES
  ('f4000001-0000-4000-8000-000000000001','e4000001-0000-4000-8000-000000000001',:tenant,'Diagnosis','E11.9', CURRENT_DATE - 1, 23),
  ('f4000001-0000-4000-8000-000000000002','e4000001-0000-4000-8000-000000000002',:tenant,'Diagnosis','I10',   CURRENT_DATE - 1, 19),
  ('f4000001-0000-4000-8000-000000000003','e4000001-0000-4000-8000-000000000003',:tenant,'Diagnosis','J06.9', CURRENT_DATE - 1, 15),
  ('f4000001-0000-4000-8000-000000000010','e4000001-0000-4000-8000-000000000010',:tenant,'Medication','A10BA02',CURRENT_DATE - 1, 31),
  ('f4000001-0000-4000-8000-000000000011','e4000001-0000-4000-8000-000000000011',:tenant,'Medication','C08CA01',CURRENT_DATE - 1, 22),
  ('f4000001-0000-4000-8000-000000000012','e4000001-0000-4000-8000-000000000012',:tenant,'Medication','N02BE01',CURRENT_DATE - 1, 40)
ON CONFLICT (fact_id) DO NOTHING;
