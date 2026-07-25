-- Dev seed: Pharmacy dispensing queue (synthetic). Approved, non-expired prescriptions with active lines so
-- /prescriptions/queue returns dispensable work. drug_id references real masterdata.drug rows.
-- Idempotent: re-runnable.

INSERT INTO pharmacy.prescription
  (prescription_id, rx_no, beneficiary_id, encounter_id, prescriber_id, status, submitted_at, expires_at) VALUES
  ('e2000000-0000-4000-8000-000000000001', 'RX-2026-0001', 'a1000000-0000-4000-8000-000000000001', 'e1000000-0000-4000-8000-000000000001', 'a592d99a-ca90-4111-aeab-e2da16469fc1', 'Approved', now() - interval '2 day', now() + interval '25 day'),
  ('e2000000-0000-4000-8000-000000000002', 'RX-2026-0002', 'a1000000-0000-4000-8000-000000000004', 'e1000000-0000-4000-8000-000000000004', 'a592d99a-ca90-4111-aeab-e2da16469fc1', 'Approved', now() - interval '1 hour', now() + interval '29 day')
ON CONFLICT (prescription_id) DO UPDATE SET status = EXCLUDED.status, expires_at = EXCLUDED.expires_at;

INSERT INTO pharmacy.prescription_line
  (prescription_line_id, prescription_id, drug_id, dose, route, frequency, quantity_prescribed, quantity_dispensed, refills_allowed, status) VALUES
  ('e3000000-0000-4000-8000-000000000001', 'e2000000-0000-4000-8000-000000000001', '40d46bd1-0200-4404-b424-d9cdd05391b4', '500 mg', 'Oral', 'BID',   60, 0, 2, 'Active'),
  ('e3000000-0000-4000-8000-000000000002', 'e2000000-0000-4000-8000-000000000002', '26d41d0b-2046-4e20-89f3-3a4a951570b7', '10 mg',  'Oral', 'Daily', 30, 0, 5, 'Active'),
  ('e3000000-0000-4000-8000-000000000003', 'e2000000-0000-4000-8000-000000000002', '3aa10944-02db-44b2-89c6-95100b09d372', '500 mg', 'Oral', 'PRN',   20, 0, 0, 'Active')
ON CONFLICT (prescription_line_id) DO UPDATE SET quantity_dispensed = 0, status = 'Active';
