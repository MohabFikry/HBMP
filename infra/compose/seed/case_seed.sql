-- Dev seed: case-management coordination data (synthetic, non-PHI). Gives the three seeded cases a coordination
-- footprint so the Case Manager portal renders live: (a) eligibility coverage for each case beneficiary so the
-- beneficiary-360 view assembles (coverage is the fail-closed spine), (b) coordination tasks, (c) escalations for
-- the cross-case escalations worklist. Idempotent: re-runnable. Beneficiary ids match case.case_file rows; the
-- created_by / raised_by actor is the seeded casemgr subject.

\set casemgr '''dfbeb51c-d591-4b8b-bdc8-3bf69d98cf51'''

-- ---- eligibility coverage for the case beneficiaries (spine of beneficiary-360) ---------------------------------
INSERT INTO eligibility.member_projection
  (beneficiary_id, member_no, given_name, family_name, status, primary_phone, updated_at) VALUES
  ('fcfb5d88-2385-4ed3-8aba-cc37f38b1fbb','MRS-M-30001','Yusuf','Haddad','Active','+201000030001', now()),
  ('b8484913-7535-455b-af50-de32bb6574d8','MRS-M-30002','Layla','Nasser','Active','+201000030002', now()),
  ('66563510-421c-4c32-94fb-84a686ecf800','MRS-M-30003','Omar','Salim', 'Active','+201000030003', now())
ON CONFLICT (beneficiary_id) DO UPDATE SET status = EXCLUDED.status, updated_at = EXCLUDED.updated_at;

INSERT INTO eligibility.coverage_projection
  (coverage_id, beneficiary_id, benefit_category, policy_no, status, effective_from, effective_to, limits_json, updated_at) VALUES
  ('c5000000-0000-4000-8000-000000000001','fcfb5d88-2385-4ed3-8aba-cc37f38b1fbb','Oncology','POL-2026-ONC-01','Active', CURRENT_DATE - 120, CURRENT_DATE + 245,
     '[{"limitType":"Annual","limitValue":250000,"consumedValue":91500}]'::jsonb, now()),
  ('c5000000-0000-4000-8000-000000000002','b8484913-7535-455b-af50-de32bb6574d8','Chronic','POL-2026-CHR-02','Active', CURRENT_DATE - 90,  CURRENT_DATE + 275,
     '[{"limitType":"Annual","limitValue":60000,"consumedValue":18200}]'::jsonb, now()),
  ('c5000000-0000-4000-8000-000000000003','66563510-421c-4c32-94fb-84a686ecf800','Outpatient','POL-2026-OUT-03','Active', CURRENT_DATE - 60, CURRENT_DATE + 305,
     '[{"limitType":"Annual","limitValue":40000,"consumedValue":9750}]'::jsonb, now())
ON CONFLICT (coverage_id) DO UPDATE SET status = EXCLUDED.status, limits_json = EXCLUDED.limits_json, updated_at = EXCLUDED.updated_at;

-- ---- coordination tasks (kanban) -------------------------------------------------------------------------------
INSERT INTO "case".coordination_task
  (task_id, case_id, title, description, assignee_id, due_at, status, created_by, created_at, updated_at, deleted) VALUES
  ('7a000000-0000-4000-8000-000000000001','a3602b8b-8ba0-4952-bbbb-650cc695b8e8','Confirm oncology authorization','Chase the pending imaging pre-auth with the approvals team.', NULL, now() + interval '2 day','Todo',       :casemgr, now(), now(), false),
  ('7a000000-0000-4000-8000-000000000002','a3602b8b-8ba0-4952-bbbb-650cc695b8e8','Schedule multidisciplinary review','Coordinate oncology + radiology joint review.',                NULL, now() + interval '5 day','InProgress', :casemgr, now(), now(), false),
  ('7a000000-0000-4000-8000-000000000003','02f10f5c-be08-47da-a23d-0ae1d2245c99','Arrange diabetic education','Book the beneficiary into the chronic-care education session.',       NULL, now() + interval '3 day','Todo',       :casemgr, now(), now(), false),
  ('7a000000-0000-4000-8000-000000000004','96c3b5da-a6be-4059-949c-1d359c7bd309','Follow up lab results','Ensure outpatient follow-up labs are returned and reviewed.',              NULL, now() - interval '1 day','Todo',       :casemgr, now(), now(), false)
ON CONFLICT (task_id) DO UPDATE SET status = EXCLUDED.status, due_at = EXCLUDED.due_at, updated_at = now();

-- ---- escalations (cross-case worklist) -------------------------------------------------------------------------
INSERT INTO "case".escalation
  (escalation_id, case_id, raised_by, raised_to_role, reason, status, raised_at) VALUES
  ('e5000000-0000-4000-8000-000000000001','a3602b8b-8ba0-4952-bbbb-650cc695b8e8', :casemgr,'medical_director','Oncology imaging pre-auth breaching SLA — director review requested.','Raised',       now() - interval '4 hour'),
  ('e5000000-0000-4000-8000-000000000002','02f10f5c-be08-47da-a23d-0ae1d2245c99', :casemgr,'medical_approval', 'Insulin pump coverage question needs an approval decision.',          'Acknowledged', now() - interval '1 day')
ON CONFLICT (escalation_id) DO UPDATE SET status = EXCLUDED.status;
