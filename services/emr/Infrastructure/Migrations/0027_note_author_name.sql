-- emr-service — 0027: record WHO wrote a clinical note, in words.
--
-- emr.emr_note has stored authored_by, a subject id, since phase 4.1. Nothing displayed it, because until 32.3
-- nothing displayed a note's authorship at all: the workspace showed the working note's TEXT and the
-- addendum mechanism had no UI, so there was never a second author on screen to distinguish from the first.
--
-- An addendum changes that. It is a correction to a SIGNED clinical record, frequently written by someone
-- other than the doctor who signed it, and the next clinician reading the note has to know which. Rendering
-- "Written by 22222222-2222-4222-8222-222222222222" is the defect 0022 fixed for appointment notes and 0020
-- for allergens — a record that displays an identifier has stopped communicating, and this is the third time
-- this platform has learned it.
--
-- Snapshot at write time, per 0022: the name is what the record MEANT at the moment it was written, and a
-- join would rewrite history every time somebody's display name changed.
--
-- Nullable: every note written before this has no captured name, and NULL says that. Readers fall back to
-- "(not recorded)" rather than to the uuid.
ALTER TABLE emr.emr_note ADD COLUMN IF NOT EXISTS authored_by_name varchar(160);

COMMENT ON COLUMN emr.emr_note.authored_by_name IS
    'Author display name as supplied by the caller''s token at write time (0027). NULL for notes written before it.';
