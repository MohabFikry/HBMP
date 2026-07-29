-- emr-service — 0009 the doctor an appointment belongs to. ADDITIVE / backward-compatible.
--
-- "The doctor sees the visits related to him" was unanswerable. The doctor link existed only on
-- emr.appointment_slot; emr.appointment had none. So a booking that names a practitioner directly — a walk-in
-- is slotless by design, and BookAppointmentRequest has carried a DoctorId all along for the
-- practitioner-at-branch check — had nowhere to record it, and the doctor's own worklist could not be a query.
--
-- Nullable on purpose: a general clinic session belongs to whoever is on shift, not to a named practitioner,
-- and that has to stay expressible. Existing rows stay NULL and behave exactly as before.

ALTER TABLE emr.appointment ADD COLUMN IF NOT EXISTS doctor_id uuid;

-- The doctor's day list is (doctor, status, time): "my checked-in patients, in arrival order".
CREATE INDEX IF NOT EXISTS ix_appointment_doctor_start ON emr.appointment (doctor_id, scheduled_start);

-- Backfill from the slot, which is where the link already lived. This is a genuine backfill rather than a
-- no-op: every slot-based booking already knows its practitioner.
UPDATE emr.appointment a
   SET doctor_id = s.doctor_id
  FROM emr.appointment_slot s
 WHERE a.slot_id = s.slot_id
   AND a.doctor_id IS NULL
   AND s.doctor_id IS NOT NULL;
