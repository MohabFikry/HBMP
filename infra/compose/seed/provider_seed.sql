-- Provider network seed (Phase 2b / Phase 9 UI). Synthetic, re-runnable. Seeds providers + locations +
-- contracts under tenant 11111111-… so the Network portal (directory / performance / contracts / locations)
-- renders real rows. The seeded `network` KC user (role network_team) is tenant-scoped (not provider-scoped),
-- so it sees the whole tenant network. Superuser seed bypasses RLS; rows carry the tenant the app filters on.
\set ten '''11111111-1111-1111-1111-111111111111'''

DELETE FROM provider.provider_contract WHERE provider_id IN ('b0000000-0000-4000-8000-000000000001','b0000000-0000-4000-8000-000000000002','b0000000-0000-4000-8000-000000000003');
DELETE FROM provider.provider_location WHERE provider_id IN ('b0000000-0000-4000-8000-000000000001','b0000000-0000-4000-8000-000000000002','b0000000-0000-4000-8000-000000000003');
DELETE FROM provider.provider          WHERE provider_id IN ('b0000000-0000-4000-8000-000000000001','b0000000-0000-4000-8000-000000000002','b0000000-0000-4000-8000-000000000003');

INSERT INTO provider.provider (provider_id, tenant_id, provider_code, legal_name, provider_type, status, onboarding_state) VALUES
  ('b0000000-0000-4000-8000-000000000001', :ten, 'PRV-0001', 'Nile Central Hospital',   'Hospital', 'Active',    'Activated'),
  ('b0000000-0000-4000-8000-000000000002', :ten, 'PRV-0002', 'Cairo Care Clinic',       'Clinic',   'Active',    'Activated'),
  ('b0000000-0000-4000-8000-000000000003', :ten, 'PRV-0003', 'Delta Diagnostics Lab',   'Lab',      'Suspended', 'Credentialed');

INSERT INTO provider.provider_location (location_id, provider_id, tenant_id, name, governorate, address, is_primary, is_deleted) VALUES
  ('b1000000-0000-4000-8000-000000000001', 'b0000000-0000-4000-8000-000000000001', :ten, 'Main Campus',   'Cairo',      '12 Nile Corniche',   true,  false),
  ('b1000000-0000-4000-8000-000000000002', 'b0000000-0000-4000-8000-000000000001', :ten, 'East Annex',    'Cairo',      '4 Salah Salem St',   false, false),
  ('b1000000-0000-4000-8000-000000000003', 'b0000000-0000-4000-8000-000000000002', :ten, 'Downtown',      'Cairo',      '7 Tahrir Sq',        true,  false);

INSERT INTO provider.provider_contract (contract_id, provider_id, tenant_id, contract_no, effective_from, effective_to, status) VALUES
  ('b2000000-0000-4000-8000-000000000001', 'b0000000-0000-4000-8000-000000000001', :ten, 'CON-2026-0001', date '2026-01-01', date '2026-12-31', 'Active'),
  ('b2000000-0000-4000-8000-000000000002', 'b0000000-0000-4000-8000-000000000002', :ten, 'CON-2026-0002', date '2026-01-01', NULL,              'Active');
