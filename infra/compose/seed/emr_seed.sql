-- Dev seed: Doctor/EMR live data (synthetic, NOT real PHI).
-- Encounters authored by the seeded `doctor` user establish the treating relationship (US-030),
-- so /encounters/mine lists them and /encounters/{id}/clinical passes the treating gate.
-- Doctor Keycloak sub: a592d99a-ca90-4111-aeab-e2da16469fc1
-- Idempotent: re-runnable.

\set doc '''a592d99a-ca90-4111-aeab-e2da16469fc1'''

INSERT INTO emr.encounter (encounter_id, encounter_no, beneficiary_id, status, started_at, created_by) VALUES
  ('e1000000-0000-4000-8000-000000000001', 'ENC-2026-0001', 'a1000000-0000-4000-8000-000000000001', 'Completed',  now() - interval '2 day',  :doc),
  ('e1000000-0000-4000-8000-000000000002', 'ENC-2026-0002', 'a1000000-0000-4000-8000-000000000002', 'InProgress', now() - interval '3 hour',  :doc),
  ('e1000000-0000-4000-8000-000000000004', 'ENC-2026-0003', 'a1000000-0000-4000-8000-000000000004', 'InProgress', now() - interval '1 hour',  :doc)
ON CONFLICT (encounter_id) DO UPDATE SET status = EXCLUDED.status, created_by = EXCLUDED.created_by;

INSERT INTO emr.emr_note (note_id, encounter_id, note_type, subjective, objective, assessment, plan, authored_by, authored_at, is_signed, signed_at) VALUES
  ('11000000-0000-4000-8000-000000000001', 'e1000000-0000-4000-8000-000000000001', 'SOAP',
     'Follow-up for type 2 diabetes; reports good adherence, occasional fatigue.',
     'BP 128/82, BMI 27.4. Feet exam normal, no ulcers.',
     'Type 2 diabetes mellitus, reasonably controlled.',
     'Continue metformin 500mg BID. HbA1c in 3 months. Dietitian referral.',
     :doc, now() - interval '2 day', true, now() - interval '2 day'),
  ('11000000-0000-4000-8000-000000000002', 'e1000000-0000-4000-8000-000000000002', 'SOAP',
     'Sore throat and runny nose for 3 days, low-grade fever.',
     'Temp 37.8C, pharynx mildly injected, no exudate. Chest clear.',
     'Acute upper respiratory infection, likely viral.',
     'Supportive care, fluids, paracetamol PRN. Return if worsening.',
     :doc, now() - interval '3 hour', false, NULL)
ON CONFLICT (note_id) DO UPDATE SET assessment = EXCLUDED.assessment, is_signed = EXCLUDED.is_signed;

INSERT INTO emr.diagnosis (diagnosis_id, encounter_id, icd_code, diagnosis_rank, clinical_status, recorded_by, recorded_at) VALUES
  ('d1000000-0000-4000-8000-000000000001', 'e1000000-0000-4000-8000-000000000001', 'E11.9', 'Primary', 'Active', :doc, now() - interval '2 day'),
  ('d1000000-0000-4000-8000-000000000002', 'e1000000-0000-4000-8000-000000000002', 'J06.9', 'Primary', 'Active', :doc, now() - interval '3 hour'),
  ('d1000000-0000-4000-8000-000000000004', 'e1000000-0000-4000-8000-000000000004', 'I10',   'Primary', 'Active', :doc, now() - interval '1 hour')
ON CONFLICT (diagnosis_id) DO NOTHING;

INSERT INTO emr.vital (vital_id, encounter_id, vital_type, value_num, unit, loinc_code, recorded_by, measured_at) VALUES
  ('71000000-0000-4000-8000-000000000001', 'e1000000-0000-4000-8000-000000000001', 'BP',     128, 'mmHg', '8480-6', :doc, now() - interval '2 day'),
  ('71000000-0000-4000-8000-000000000002', 'e1000000-0000-4000-8000-000000000001', 'HR',      74, 'bpm',  '8867-4', :doc, now() - interval '2 day'),
  ('71000000-0000-4000-8000-000000000003', 'e1000000-0000-4000-8000-000000000001', 'Temp',  36.7, 'Cel',  '8310-5', :doc, now() - interval '2 day'),
  ('71000000-0000-4000-8000-000000000004', 'e1000000-0000-4000-8000-000000000001', 'Weight',  78, 'kg',   '29463-7', :doc, now() - interval '2 day'),
  ('71000000-0000-4000-8000-000000000005', 'e1000000-0000-4000-8000-000000000001', 'Height', 169, 'cm',   '8302-2', :doc, now() - interval '2 day'),
  ('71000000-0000-4000-8000-000000000006', 'e1000000-0000-4000-8000-000000000002', 'Temp',  37.8, 'Cel',  '8310-5', :doc, now() - interval '3 hour'),
  ('71000000-0000-4000-8000-000000000007', 'e1000000-0000-4000-8000-000000000002', 'HR',      88, 'bpm',  '8867-4', :doc, now() - interval '3 hour')
ON CONFLICT (vital_id) DO NOTHING;

INSERT INTO emr.allergy (allergy_id, beneficiary_id, allergen_id, reaction, severity, status, recorded_by, recorded_at) VALUES
  ('a2000000-0000-4000-8000-000000000001', 'a1000000-0000-4000-8000-000000000001', 'f0000000-0000-4000-8000-000000000001', 'Penicillin — rash', 'Moderate', 'Active', :doc, now() - interval '2 day')
ON CONFLICT (allergy_id) DO NOTHING;
