-- Dev seed: Lab & Imaging fulfillment queue (synthetic). Orders are Active with available lines so the
-- capability-filtered provider queue (/investigation-orders/queue) returns work for lab_tech / imaging_tech.
-- Idempotent: re-runnable.

INSERT INTO orders.investigation_order
  (order_id, order_no, beneficiary_id, encounter_id, ordering_provider_id, order_type, status, requested_at) VALUES
  ('05000000-0000-4000-8000-000000000001', 'ORD-2026-0001', 'a1000000-0000-4000-8000-000000000001', 'e1000000-0000-4000-8000-000000000001', 'b0000000-0000-4000-8000-000000000001', 'Lab',     'Active', now() - interval '2 day'),
  ('05000000-0000-4000-8000-000000000002', 'ORD-2026-0002', 'a1000000-0000-4000-8000-000000000002', 'e1000000-0000-4000-8000-000000000002', 'b0000000-0000-4000-8000-000000000001', 'Lab',     'Active', now() - interval '3 hour'),
  ('05000000-0000-4000-8000-000000000003', 'ORD-2026-0003', 'a1000000-0000-4000-8000-000000000004', 'e1000000-0000-4000-8000-000000000004', 'b0000000-0000-4000-8000-000000000002', 'Imaging', 'Active', now() - interval '1 hour')
ON CONFLICT (order_id) DO UPDATE SET status = EXCLUDED.status;

INSERT INTO orders.order_line
  (order_line_id, order_id, code_system, code, description, quantity_ordered, quantity_consumed, status) VALUES
  ('06000000-0000-4000-8000-000000000001', '05000000-0000-4000-8000-000000000001', 'LOINC', '58410-2', 'CBC panel - Blood', 1, 0, 'Active'),
  ('06000000-0000-4000-8000-000000000002', '05000000-0000-4000-8000-000000000002', 'LOINC', '2345-7',  'Glucose [Mass/volume] in Serum or Plasma', 1, 0, 'Active'),
  ('06000000-0000-4000-8000-000000000003', '05000000-0000-4000-8000-000000000002', 'LOINC', '2093-3',  'Cholesterol [Mass/volume] in Serum or Plasma', 1, 0, 'Active'),
  ('06000000-0000-4000-8000-000000000004', '05000000-0000-4000-8000-000000000003', 'CPT',   '71046',   'Radiologic examination, chest; 2 views', 1, 0, 'Active')
ON CONFLICT (order_line_id) DO UPDATE SET quantity_consumed = 0, status = 'Active';
