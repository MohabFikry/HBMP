-- emr-service — 0011 the booking note. ADDITIVE / backward-compatible.
--
-- ============================================================================================================
-- WHAT THIS IS, AND — MORE IMPORTANTLY — WHAT IT IS NOT
-- ============================================================================================================
-- A short GENERAL / ADMINISTRATIVE note captured at booking: "patient uses a wheelchair, book the ground-floor
-- room", "interpreter needed — Tigrinya", "sister will attend, same slot if possible". It is entered by
-- reception or the call centre, and read by reception, the call centre and the treating doctor.
--
-- It is NOT a clinical note. Nothing here is a symptom, a diagnosis, a medication or a result. That boundary is
-- load-bearing rather than decorative, because this column is readable across a line the platform otherwise
-- enforces hard: the CALL CENTRE can write it and a DOCTOR can read it, and the call centre is deliberately
-- given no clinical surface anywhere else on the platform (11-permission-matrix; callcentre-service holds no
-- emr scope at all). A free-text field readable across that line is exactly where clinical detail would
-- accumulate if nobody said otherwise — an agent typing "says she is bleeding again" would have created, in
-- effect, an unaudited clinical record written by someone with no clinical authority and no treating
-- relationship.
--
-- So the constraints below are the boundary made structural rather than advisory:
--
--   * LENGTH CAP (500). An arrangement fits; a history does not. The cap is enforced in the database and not
--     only in the API, because the API is not the only writer a schema outlives.
--   * The column lives on the APPOINTMENT, not on the encounter, and is projected by the appointment
--     endpoints only. It is therefore never part of any clinical read, never lands in the FHIR projection,
--     and cannot be reached with emr:read alone.
--
-- Access control note: this column rides on appointment reads, which are already branch-scoped (14.4) and
-- min-necessary. No new scope is introduced — a caller who may see the appointment may see its note, which is
-- precisely the sharing the three teams asked for.

ALTER TABLE emr.appointment ADD COLUMN IF NOT EXISTS note varchar(500);

COMMENT ON COLUMN emr.appointment.note IS
    'General/administrative booking note (access needs, arrangements, preferences). NOT clinical: no symptoms, '
    'diagnoses, medications or results. Readable by reception, the call centre and the treating doctor.';
