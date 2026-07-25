-- Dev seed: Medical approvals worklist (synthetic). Submitted authorizations with SLA timers so the
-- worklist (/authorizations/) returns pending review work. Idempotent: re-runnable.

INSERT INTO approvals.authorization
  (authorization_id, auth_no, beneficiary_id, source, source_ref, requesting_provider_id, service_codes, requested_scope,
   priority, status, sla_due_at, submitted_at, sla_breached) VALUES
  ('a4000000-0000-4000-8000-000000000001', 'AUTH-2026-0001', 'a1000000-0000-4000-8000-000000000001', 'OrderLine', 'ORD-2026-0009',
     'b0000000-0000-4000-8000-000000000002', '["70553"]'::jsonb, '{"kind":"Imaging"}'::jsonb,
     'Urgent',    'Submitted', now() + interval '6 hour',  now() - interval '2 hour', false),
  ('a4000000-0000-4000-8000-000000000002', 'AUTH-2026-0002', 'a1000000-0000-4000-8000-000000000002', 'OrderLine', 'ORD-2026-0010',
     'b0000000-0000-4000-8000-000000000002', '["71046"]'::jsonb, '{"kind":"Imaging"}'::jsonb,
     'Routine',   'Submitted', now() + interval '46 hour', now() - interval '3 hour', false),
  ('a4000000-0000-4000-8000-000000000003', 'AUTH-2026-0003', 'a1000000-0000-4000-8000-000000000004', 'OrderLine', 'ORD-2026-0011',
     'b0000000-0000-4000-8000-000000000002', '["29881"]'::jsonb, '{"kind":"Procedure"}'::jsonb,
     'Emergency', 'Submitted', now() - interval '1 hour',  now() - interval '5 hour', true)
ON CONFLICT (authorization_id) DO UPDATE SET status = EXCLUDED.status, sla_due_at = EXCLUDED.sla_due_at, sla_breached = EXCLUDED.sla_breached;
