-- emr-service — 0010 who performed the last transition. ADDITIVE / backward-compatible.
--
-- emr.appointment_history already snapshots the whole row on every insert and update, so WHAT changed and WHEN
-- were both recoverable — but the row carried only created_by, so every transition after booking was anonymous.
-- A visit timeline that cannot say who checked the patient in or who marked the no-show is not a timeline
-- anyone can act on, and "the desk says it was already marked" has no answer without it.
--
-- The compliance audit store (audit-service, hash-chained) remains the authoritative record and is reachable
-- only with audit:read — Security/Compliance/DPO. This column exists so the OPERATIONAL timeline can be shown
-- to the desk and the treating clinician under appointment:read, without handing them the audit surface.

ALTER TABLE emr.appointment ADD COLUMN IF NOT EXISTS updated_by text;

-- Existing rows stay NULL: their transitions genuinely were not attributed, and inventing an actor for them
-- would be worse than showing none.
