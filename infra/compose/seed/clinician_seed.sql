-- Clinician worklist seed (Phase 4 / Phase 9 UI). Attributes existing synthetic orders/prescriptions to the
-- seeded `doctor` KC user (sub a592d99a-…) so the doctor portal's my-orders / prescriptions / results-inbox
-- render real rows via /investigation-orders/mine and /prescriptions/mine (both scoped by created_by == sub).
-- Also marks one order Completed so the Results inbox (?status=Completed) is non-empty. Dev-only, re-runnable.
\set doc '''a592d99a-ca90-4111-aeab-e2da16469fc1'''

UPDATE orders.investigation_order SET created_by = :doc WHERE created_by IS NULL;
UPDATE pharmacy.prescription     SET created_by = :doc WHERE created_by IS NULL;

-- Mark the oldest of the doctor's orders Completed (results are back) — and its lines too.
WITH pick AS (
  SELECT order_id FROM orders.investigation_order
  WHERE created_by = :doc AND status <> 'Completed'
  ORDER BY requested_at ASC LIMIT 1
)
UPDATE orders.investigation_order o SET status = 'Completed'
  FROM pick WHERE o.order_id = pick.order_id;
