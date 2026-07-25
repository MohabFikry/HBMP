-- Nurse portal seed (Phase 4 / Phase 9 UI). Seeds encounters OWNED by the seeded `nurse` KC user
-- (sub 3f2e3504-…) so /encounters/mine returns them AND the treating-relationship gate (encounter.created_by ==
-- subject) lets the nurse read the clinical record and record vitals. Reuses eligibility beneficiary ids.
-- Dev-only, re-runnable.
\set nurse '''3f2e3504-c1f2-4870-b3b8-2391ba3ef85b'''

DELETE FROM emr.vital     WHERE encounter_id IN ('e0000000-0000-4000-8000-0000000000e1','e0000000-0000-4000-8000-0000000000e2');
DELETE FROM emr.encounter WHERE encounter_id IN ('e0000000-0000-4000-8000-0000000000e1','e0000000-0000-4000-8000-0000000000e2');

INSERT INTO emr.encounter (encounter_id, encounter_no, beneficiary_id, provider_id, status, started_at, created_by)
VALUES
  ('e0000000-0000-4000-8000-0000000000e1', 'ENC-2026-0N01', 'a1000000-0000-4000-8000-000000000001',
   'b0000000-0000-4000-8000-000000000001', 'InProgress', now() - interval '30 minutes', :nurse),
  ('e0000000-0000-4000-8000-0000000000e2', 'ENC-2026-0N02', 'a1000000-0000-4000-8000-000000000002',
   'b0000000-0000-4000-8000-000000000001', 'InProgress', now() - interval '10 minutes', :nurse);
