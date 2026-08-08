-- emr-service — 0014 who wrote the booking note, and when. ADDITIVE.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- The note is free text written by one team and read by two others: reception and the call centre write it,
-- and the treating doctor reads it. Until now it arrived on screen with no author and no date, so a clinician
-- reading "patient will bring their sister to interpret" had no way to tell whether that was agreed this
-- morning or six weeks ago at the original booking — and no way to ask the person who wrote it.
--
-- An unattributed instruction that crosses a team boundary is one nobody can follow up, and the one most
-- likely to be acted on when it is stale. So the note carries its authorship with it.
--
-- The AUDIT trail already records that an edit happened (`ApptNoteEdited`), but audit-service needs
-- `audit:read` — Security, Compliance, DPO — and is not reachable by the desk or the doctor who are the ones
-- asking "who told us this?". These two columns answer that question at the point the note is read, under the
-- scope the reader already holds. They are display attribution, not a second audit trail.

ALTER TABLE emr.appointment ADD COLUMN IF NOT EXISTS note_by text;
ALTER TABLE emr.appointment ADD COLUMN IF NOT EXISTS note_at timestamptz;

COMMENT ON COLUMN emr.appointment.note_by IS
    'Subject id of whoever last wrote the booking note. Display attribution for the desk and the treating '
    'doctor — NOT the audit trail, which lives in audit-service behind audit:read.';
COMMENT ON COLUMN emr.appointment.note_at IS 'When the booking note was last written.';

-- Existing notes stay unattributed. Stamping them with the row's updated_by/updated_at would claim whoever
-- last touched the appointment wrote the note, which is exactly the false attribution these columns exist to
-- prevent — a clinician would then chase the wrong person about an instruction they never gave.
