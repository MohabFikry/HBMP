-- emr-service — 0006 branch scoping (phase 14.4, design 37 §3). ADDITIVE / backward-compatible.
-- Adds branch_id alongside the existing location_id on the operational scheduling tables. A booking at a
-- Mersal branch sets branch_id; a booking at an external provider location leaves it NULL (branch scoping
-- applies only to branch-bound rows). Existing rows default to NULL and behave exactly as before — the
-- phase-3 no-double-book invariant (ux_appointment_active_slot) is UNTOUCHED.

ALTER TABLE emr.appointment           ADD COLUMN IF NOT EXISTS branch_id uuid;
ALTER TABLE emr.appointment_slot      ADD COLUMN IF NOT EXISTS branch_id uuid;
ALTER TABLE emr.provider_availability ADD COLUMN IF NOT EXISTS branch_id uuid;
ALTER TABLE emr.waitlist_entry        ADD COLUMN IF NOT EXISTS branch_id uuid;
ALTER TABLE emr.appointment_queue     ADD COLUMN IF NOT EXISTS branch_id uuid;

-- Worklist indexes: (branch, scheduled_start) for appointment lists, (branch, state) for queues.
CREATE INDEX IF NOT EXISTS ix_appointment_branch_start ON emr.appointment (branch_id, scheduled_start);
CREATE INDEX IF NOT EXISTS ix_queue_branch_state       ON emr.appointment_queue (branch_id, state);

-- NOTE: no backfill mapping location_id → branch_id is possible in this environment (the location↔branch
-- map is operator-curated); existing rows stay NULL and are surfaced only to member-scoped roles until an
-- operator reconciles them. New bookings carry branch_id from the active branch (design 37 §3).
