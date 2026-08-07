-- emr-service — 0022: record WHO wrote the booking note, in words.
--
-- 0014 added `note_by` to answer "who told us this?" at the point the note is read. It stores the author's
-- SUBJECT ID, and the subject id is what reached the screen: the note dialog rendered
--
--     Written by c18b985c-cc5f-42eb-8b79-e41b7b84f975 · 06 Aug 2026, 10:38
--
-- which answers the question with a string nobody at a reception desk can act on. 0014's own rationale was
-- that "an unattributed instruction that crosses a team boundary is one nobody can follow up" — a uuid is
-- unattributed in every sense that matters to the person reading it.
--
-- The name is SNAPSHOT at write time rather than joined at read time, following 19.3 (signatures are
-- snapshotted, never joined) and 0020 (allergen_display), for the same two reasons:
--   1. It costs no cross-service call. Resolving an author id would mean emr-service calling identity-service
--      once per note on every board read, putting an authentication dependency in a clinical read path that
--      has none today.
--   2. It is the more honest record. The question is who wrote this instruction, and the answer is the person
--      as they were named at the moment they wrote it — not whatever they are renamed to, or a blank where a
--      de-provisioned account used to be.
--
-- `note_by` STAYS. It is the authoritative link, it is what the audit trail correlates on, and a display name
-- is not an identity. This column is only what the reader is shown.
--
-- Nullable, deliberately: notes written before this migration have no captured name, and NULL says exactly
-- that. Back-filling them from `note_by` would put the uuid back on screen wearing a different column name,
-- and back-filling from the identity directory would claim the author's CURRENT name for a note written
-- before it — the same false attribution 0014 refused when it declined to stamp existing notes with
-- `updated_by`. Readers fall back to "unknown", which is true.
ALTER TABLE emr.appointment ADD COLUMN IF NOT EXISTS note_by_name varchar(160);

COMMENT ON COLUMN emr.appointment.note_by_name IS
    'Display name of whoever last wrote the booking note, captured at the moment of writing. NULL for notes '
    'written before 0022. Display attribution only — note_by remains the authoritative identity.';
