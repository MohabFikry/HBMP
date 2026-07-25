-- Dev seed: admin & platform-governance data (synthetic, non-PHI). Populates the tenant registry, role-binding
-- access matrix, an access-review campaign and a break-glass grant so the Admin portal renders live governance
-- data. Superuser insert bypasses RLS (the app role is tenant/super-admin scoped). Idempotent: re-runnable.
-- Actor ids are the seeded superadmin/orgadmin Keycloak subjects; subject_user_id values are the role users.

\set tenant '''11111111-1111-1111-1111-111111111111'''
\set superadmin '''48fad97e-fc08-48f2-8fbd-4b15815a5aef'''
\set orgadmin '''23e1ae7e-0686-4962-a8f9-89753d4332b8'''

-- ---- tenant registry -------------------------------------------------------------------------------------------
INSERT INTO admin.tenant (tenant_id, name, active, created_by, created_at) VALUES
  (:tenant, 'Mersal Foundation', true, :superadmin, now())
ON CONFLICT (tenant_id) DO UPDATE SET name = EXCLUDED.name, active = EXCLUDED.active;

-- ---- role bindings (the access matrix) -------------------------------------------------------------------------
-- One active binding per seeded user → the access-matrix lists who holds what. Tiers reflect data sensitivity.
INSERT INTO admin.role_binding
  (binding_id, tenant_id, subject_user_id, role, scope_type, provider_id, tier, granted_by, justification, granted_at, review_due_at, status)
VALUES
  ('b1000000-0000-4000-8000-000000000001', :tenant, 'c0cee41d-066a-4686-b60b-c2614b6a9a88', 'reception',        'Tenant', NULL, 'T2', :orgadmin,  'Front-desk eligibility & check-in.',        now() - interval '30 day', now() + interval '60 day', 'Active'),
  ('b1000000-0000-4000-8000-000000000002', :tenant, 'a592d99a-ca90-4111-aeab-e2da16469fc1', 'doctor',           'Tenant', NULL, 'T4', :orgadmin,  'Treating clinician — clinical EMR access.', now() - interval '30 day', now() + interval '60 day', 'Active'),
  ('b1000000-0000-4000-8000-000000000003', :tenant, 'feaad650-9426-41fc-81c3-000dd6db5ca1', 'pharmacist',       'Tenant', NULL, 'T3', :orgadmin,  'Dispensing at network pharmacy.',           now() - interval '30 day', now() + interval '60 day', 'Active'),
  ('b1000000-0000-4000-8000-000000000004', :tenant, '6648361f-3844-4b18-b6b4-f87fbcba0482', 'medical_approval', 'Tenant', NULL, 'T4', :orgadmin,  'Authorization review & decisions.',         now() - interval '30 day', now() + interval '60 day', 'Active'),
  ('b1000000-0000-4000-8000-000000000005', :tenant, '76d76804-96b8-4ac1-907b-87bbce8c662b', 'finance',          'Tenant', NULL, 'T2', :orgadmin,  'Finance — utilization & settlements.',      now() - interval '30 day', now() + interval '60 day', 'Active'),
  ('b1000000-0000-4000-8000-000000000006', :tenant, 'dfbeb51c-d591-4b8b-bdc8-3bf69d98cf51', 'case_manager',     'Tenant', NULL, 'T4', :orgadmin,  'Complex-case coordination.',                now() - interval '30 day', now() + interval '60 day', 'Active'),
  ('b1000000-0000-4000-8000-000000000007', :tenant, 'ac35709e-517c-47bb-a966-e5fce09e070b', 'medical_director', 'Tenant', NULL, 'T4', :superadmin,'Clinical oversight & escalations.',         now() - interval '30 day', now() + interval '60 day', 'Active'),
  ('b1000000-0000-4000-8000-000000000008', :tenant, '23e1ae7e-0686-4962-a8f9-89753d4332b8', 'org_admin',        'Tenant', NULL, 'T3', :superadmin,'Tenant administration.',                    now() - interval '30 day', now() + interval '60 day', 'Active')
ON CONFLICT (binding_id) DO UPDATE SET status = EXCLUDED.status, review_due_at = EXCLUDED.review_due_at;

-- ---- access-review campaign ------------------------------------------------------------------------------------
INSERT INTO admin.access_review_campaign (campaign_id, tenant_id, name, min_tier, created_at, created_by, due_at, status) VALUES
  ('ca100000-0000-4000-8000-000000000001', :tenant, 'Q3 2026 high-sensitivity access recertification', 'T3', now() - interval '5 day', :superadmin, now() + interval '9 day', 'Open')
ON CONFLICT (campaign_id) DO UPDATE SET status = EXCLUDED.status, due_at = EXCLUDED.due_at;

-- ---- break-glass grant (governance dashboard) ------------------------------------------------------------------
INSERT INTO admin.break_glass_grant
  (grant_id, tenant_id, requester_user_id, reason_code, justification, scoped_resource_types, scoped_resource_ids,
   window_minutes, status, approver_user_id, approved_at, step_up_satisfied, activated_at, not_before, expires_at, requested_at, post_review_done)
VALUES
  ('b6100000-0000-4000-8000-000000000001', :tenant, 'a592d99a-ca90-4111-aeab-e2da16469fc1', 'EmergencyCare',
     'Unconscious beneficiary in ED — need records without an active treating relationship.',
     '["emr"]'::jsonb, '[]'::jsonb, 60, 'Expired', 'ac35709e-517c-47bb-a966-e5fce09e070b',
     now() - interval '2 day', true, now() - interval '2 day', now() - interval '2 day', now() - interval '2 day' + interval '60 minute',
     now() - interval '2 day', true)
ON CONFLICT (grant_id) DO UPDATE SET status = EXCLUDED.status;
