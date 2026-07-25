-- Reception day-board seed (Phase 3 / Phase 9 UI). Synthetic, re-runnable. Seeds TODAY's appointments so the
-- reception portal's Visits / Appointments / Check-in screens render real rows via GET /api/v1/appointments.
-- Reuses the eligibility seed's beneficiary ids (a1000000-…-000{1..4}). appointment_type is constrained to
-- WalkIn|Scheduled|Referral|FollowUp (FollowUp needs origin_encounter_id, Referral needs referral_ref → we use
-- Scheduled/WalkIn). No FK on provider/location, so demo ids are fine.
DELETE FROM emr.appointment WHERE appointment_id IN (
  'ad000000-0000-4000-8000-000000000001',
  'ad000000-0000-4000-8000-000000000002',
  'ad000000-0000-4000-8000-000000000003',
  'ad000000-0000-4000-8000-000000000004'
);

INSERT INTO emr.appointment
  (appointment_id, beneficiary_id, provider_id, location_id, appointment_type, status, scheduled_start, scheduled_end)
VALUES
  ('ad000000-0000-4000-8000-000000000001', 'a1000000-0000-4000-8000-000000000001',
   '22222222-0000-4000-8000-000000000001', '33333333-0000-4000-8000-000000000001',
   'Scheduled', 'Booked',    date_trunc('day', now()) + interval '9 hours',            date_trunc('day', now()) + interval '9 hours 20 minutes'),
  ('ad000000-0000-4000-8000-000000000002', 'a1000000-0000-4000-8000-000000000002',
   '22222222-0000-4000-8000-000000000001', '33333333-0000-4000-8000-000000000001',
   'Scheduled', 'CheckedIn', date_trunc('day', now()) + interval '9 hours 30 minutes', date_trunc('day', now()) + interval '9 hours 50 minutes'),
  ('ad000000-0000-4000-8000-000000000003', 'a1000000-0000-4000-8000-000000000003',
   '22222222-0000-4000-8000-000000000001', '33333333-0000-4000-8000-000000000001',
   'WalkIn',    'Booked',    date_trunc('day', now()) + interval '10 hours',           date_trunc('day', now()) + interval '10 hours 20 minutes'),
  ('ad000000-0000-4000-8000-000000000004', 'a1000000-0000-4000-8000-000000000004',
   '22222222-0000-4000-8000-000000000001', '33333333-0000-4000-8000-000000000001',
   'Scheduled', 'NoShow',    date_trunc('day', now()) + interval '8 hours 30 minutes', date_trunc('day', now()) + interval '8 hours 50 minutes');
