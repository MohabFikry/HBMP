-- Dev seed: in-app notification inbox (synthetic, non-PHI). Each row is a Channel=InApp notification addressed to a
-- seeded role user's Keycloak subject, so the Notifications portal renders a live inbox per role. Subjects/bodies
-- carry only min-necessary business keys (never clinical content). Idempotent: re-runnable.

\set tenant '''11111111-1111-1111-1111-111111111111'''

INSERT INTO notification.notification
  (notification_id, tenant_id, recipient_user_id, recipient_role, channel, locale, template_key, subject, body,
   status_text, source_event_id, source_event_type, entity_ref, sensitive, status, attempts, created_at, delivered_at, read_at, actionable)
VALUES
  -- Doctor (a592d99a)
  ('d0000000-0000-4000-8000-000000000001', :tenant, 'a592d99a-ca90-4111-aeab-e2da16469fc1', 'doctor', 'InApp', 'en', 'result.ready',
     'Lab result ready', 'A lab result for one of your encounters is ready to review.', 'Action needed',
     'd0000000-0000-4000-8000-0000000000a1', 'OrderResulted', 'ORD-2026-000117', false, 'Delivered', 1, now() - interval '2 hour', now() - interval '2 hour', NULL, true),
  ('d0000000-0000-4000-8000-000000000002', :tenant, 'a592d99a-ca90-4111-aeab-e2da16469fc1', 'doctor', 'InApp', 'en', 'auth.decided',
     'Authorization approved', 'An authorization you requested has been approved.', 'Approved',
     'd0000000-0000-4000-8000-0000000000a2', 'AuthorizationDecided', 'AUTH-2026-0002', false, 'Delivered', 1, now() - interval '1 day', now() - interval '1 day', now() - interval '20 hour', false),
  -- Pharmacist (feaad650)
  ('d0000000-0000-4000-8000-000000000010', :tenant, 'feaad650-9426-41fc-81c3-000dd6db5ca1', 'pharmacist', 'InApp', 'en', 'rx.submitted',
     'New prescription to dispense', 'A prescription has been submitted and is ready for dispensing.', 'Action needed',
     'd0000000-0000-4000-8000-0000000000b1', 'PrescriptionSubmitted', 'RX-2026-0001', false, 'Delivered', 1, now() - interval '3 hour', now() - interval '3 hour', NULL, true),
  -- Medical approver (6648361f)
  ('d0000000-0000-4000-8000-000000000020', :tenant, '6648361f-3844-4b18-b6b4-f87fbcba0482', 'medical_approval', 'InApp', 'en', 'auth.pending',
     'Authorization awaiting review', 'A new authorization is on your worklist and awaiting a decision.', 'Action needed',
     'd0000000-0000-4000-8000-0000000000c1', 'AuthorizationSubmitted', 'AUTH-2026-0001', false, 'Delivered', 1, now() - interval '90 minute', now() - interval '90 minute', NULL, true),
  ('d0000000-0000-4000-8000-000000000021', :tenant, '6648361f-3844-4b18-b6b4-f87fbcba0482', 'medical_approval', 'InApp', 'en', 'auth.sla',
     'Authorization breaching SLA', 'An emergency authorization is past its response window.', 'Escalated',
     'd0000000-0000-4000-8000-0000000000c2', 'AuthorizationSlaBreached', 'AUTH-2026-0003', false, 'Delivered', 1, now() - interval '30 minute', now() - interval '30 minute', NULL, true),
  -- Case manager (dfbeb51c)
  ('d0000000-0000-4000-8000-000000000030', :tenant, 'dfbeb51c-d591-4b8b-bdc8-3bf69d98cf51', 'case_manager', 'InApp', 'en', 'case.escalation',
     'Escalation acknowledged', 'The approvals team acknowledged your insulin-pump escalation.', 'Informational',
     'd0000000-0000-4000-8000-0000000000d1', 'EscalationAcknowledged', 'CASE-2026-000002', false, 'Delivered', 1, now() - interval '5 hour', now() - interval '5 hour', NULL, false),
  ('d0000000-0000-4000-8000-000000000031', :tenant, 'dfbeb51c-d591-4b8b-bdc8-3bf69d98cf51', 'case_manager', 'InApp', 'en', 'task.due',
     'Coordination task due', 'A follow-up task on CASE-2026-000003 is now due.', 'Action needed',
     'd0000000-0000-4000-8000-0000000000d2', 'TaskDue', 'CASE-2026-000003', false, 'Delivered', 1, now() - interval '10 hour', now() - interval '10 hour', NULL, true),
  -- Director (ac35709e)
  ('d0000000-0000-4000-8000-000000000040', :tenant, 'ac35709e-517c-47bb-a966-e5fce09e070b', 'medical_director', 'InApp', 'en', 'escalation.director',
     'Escalation raised to you', 'An oncology pre-auth was escalated for director review.', 'Action needed',
     'd0000000-0000-4000-8000-0000000000e1', 'EscalationRaised', 'CASE-2026-000001', false, 'Delivered', 1, now() - interval '4 hour', now() - interval '4 hour', NULL, true),
  -- Reception (c0cee41d)
  ('d0000000-0000-4000-8000-000000000050', :tenant, 'c0cee41d-066a-4686-b60b-c2614b6a9a88', 'reception', 'InApp', 'en', 'appt.reminder',
     'Appointments today', 'You have appointments scheduled for today''s clinic.', 'Informational',
     'd0000000-0000-4000-8000-0000000000f1', 'AppointmentReminder', NULL, false, 'Delivered', 1, now() - interval '6 hour', now() - interval '6 hour', now() - interval '5 hour', false),
  -- Lab tech (525d1cca)
  ('d0000000-0000-4000-8000-000000000060', :tenant, '525d1cca-4eb8-403a-a883-dfebd43a79be', 'lab_tech', 'InApp', 'en', 'order.queued',
     'New lab order in queue', 'A lab order has been routed to your queue.', 'Action needed',
     'd0000000-0000-4000-8000-000000000101', 'OrderActivated', 'ORD-2026-000117', false, 'Delivered', 1, now() - interval '80 minute', now() - interval '80 minute', NULL, true),
  -- Finance (76d76804)
  ('d0000000-0000-4000-8000-000000000070', :tenant, '76d76804-96b8-4ac1-907b-87bbce8c662b', 'finance', 'InApp', 'en', 'settlement.ready',
     'Provider settlement ready', 'A provider settlement batch is ready for review.', 'Informational',
     'd0000000-0000-4000-8000-000000000111', 'SettlementPrepared', 'SETL-2026-0007', false, 'Delivered', 1, now() - interval '1 day', now() - interval '1 day', NULL, false)
ON CONFLICT (notification_id) DO UPDATE SET read_at = EXCLUDED.read_at, status = EXCLUDED.status;
