-- ============================================================================================================
-- seed-doctor-account.sql — a working doctor: one login, one practitioner record, a specialty, two branches,
-- a roster, and a day's clinic in mixed states.
--
--   psql -h localhost -p 55432 -U hbmp -d hbmp -v ON_ERROR_STOP=1 -f tools/dev/seed-doctor-account.sql
--
-- Run AFTER restore-reference-structure.sql and seed-dev-clinic.sql — it books this doctor's clinic with the
-- beneficiaries those files created, and fails loudly below if they are missing.
--
-- ============================================================================================================
-- THE ONE THING THAT MAKES THIS WORK: practitioner_id = the LOGIN's user id
-- ============================================================================================================
-- Three places decide what a doctor may see, and all three compare an appointment's `doctor_id` against the
-- SUBJECT OF THE ACCESS TOKEN — never against a client-supplied id, which is the correct security choice:
--
--   emr GET /appointments?mine=true   →  q.Where(a => a.DoctorId == Guid.Parse(me.Principal.Subject))
--   emr POST /encounters              →  VisitStartRules.MayStart(appt, callerId)   ("not-the-assigned-doctor")
--   emr GET /encounters/mine          →  e.CreatedBy == p.Subject                   (the "My Patients" panel)
--
-- Meanwhile the booking screen writes `doctor_id` from the value provider-service hands it, which is
-- `PractitionerView.PractitionerId` (see apps/web/src/screens/booking/bookableDoctors.ts — `p.id`).
--
-- Put those together and the platform requires practitioner_id == identity user id. Nothing enforces it, so
-- it is easy to seed a practitioner that no one can ever log in as — which is exactly what the six existing
-- practitioners are. Their `user_id` column holds slugs (`seed-dr-hala`, `demo-dr-hana`) that match no
-- account, and their `practitioner_id`s are hand-chosen uuids, so every appointment booked against them is
-- invisible to every doctor login: "My Visits" renders empty and "Start visit" answers 403. They are fine as
-- BOOKABLE names in a picker; none of them is a person who can sign in.
--
-- This file closes that gap for one doctor by deriving the practitioner_id from the account instead of
-- inventing it. `user_id` is set to the same uuid rather than a slug, so the two columns agree and the link
-- is legible from either side.
--
-- The id is READ from identity."user" rather than written, because UserSeeder mints it with Guid.NewGuid()
-- on first startup — it differs per environment, and hardcoding today's value here would produce a file that
-- works on this machine and silently seeds an orphan practitioner on any other.
--
-- SYNTHETIC. Dr Karim Abdel-Latif is invented, like every patient he sees here. CLAUDE.md: never real PHI in
-- lower environments.
-- ============================================================================================================

\set ON_ERROR_STOP on

SET app.tenant_id = '11111111-1111-1111-1111-111111111111';

BEGIN;

CREATE OR REPLACE FUNCTION pg_temp.did(label text) RETURNS uuid LANGUAGE sql IMMUTABLE AS $$
    SELECT (substr(m,1,8)||'-'||substr(m,9,4)||'-4'||substr(m,14,3)||'-a'||substr(m,18,3)||'-'||substr(m,21,12))::uuid
    FROM (SELECT md5('mersal-dev-seed:'||label) AS m) s;
$$;

-- ── Prerequisites ───────────────────────────────────────────────────────────────────────────────────────────
DO $need$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM identity."user" WHERE user_name = 'doctor') THEN
        RAISE EXCEPTION 'no `doctor` account — identity-service seeds it at startup (Issuer:SeedDemoUsers)';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM provider.branch WHERE branch_code = 'DOK') THEN
        RAISE EXCEPTION 'branches missing — run tools/dev/restore-reference-structure.sql first';
    END IF;
    IF (SELECT count(*) FROM patient.beneficiary WHERE status = 'Active') < 10 THEN
        RAISE EXCEPTION 'not enough active beneficiaries to fill a clinic — run tools/dev/seed-dev-clinic.sql first';
    END IF;
END
$need$;

-- The account, resolved once and reused. A table rather than a variable so the INSERTs below can join it.
CREATE TEMP TABLE doc ON COMMIT DROP AS
SELECT id AS uid, id::text AS uid_text FROM identity."user" WHERE user_name = 'doctor';

-- The desk, for the same reason: a check-in is performed by reception, and the appointment timeline resolves
-- its actors through identity's user-labels endpoint. Attributing it to a literal like 'seed:reception' would
-- render as an unresolvable id chip on every seeded appointment — a real account resolves to a real name, so
-- the timeline demonstrates what it is for instead of demonstrating its fallback.
CREATE TEMP TABLE desk ON COMMIT DROP AS
SELECT id::text AS uid_text FROM identity."user" WHERE user_name = 'reception';

-- ── Clear this doctor's previous seeding ────────────────────────────────────────────────────────────────────
-- Re-running on a later day would otherwise leave the old board behind: yesterday's "Booked" appointments
-- sitting in the past, two sets of slots on the same roster. Scoped strictly to rows this file created — the
-- doctor's own appointments and the encounters opened from them. Nothing else in the database is touched.
DELETE FROM emr.vital       WHERE encounter_id IN (SELECT e.encounter_id FROM emr.encounter e, doc d WHERE e.created_by = d.uid_text);
DELETE FROM emr.diagnosis   WHERE encounter_id IN (SELECT e.encounter_id FROM emr.encounter e, doc d WHERE e.created_by = d.uid_text);
DELETE FROM emr.emr_note    WHERE encounter_id IN (SELECT e.encounter_id FROM emr.encounter e, doc d WHERE e.created_by = d.uid_text);
DELETE FROM emr.queue_entry WHERE encounter_id IN (SELECT e.encounter_id FROM emr.encounter e, doc d WHERE e.created_by = d.uid_text);
-- The care episode (ADR-0031) goes too, and for the same reason as the history below: the appointment ids are
-- derived from the slot, so a re-created appointment would inherit the previous run's steps — orders placed
-- in a visit that no longer exists, appended to a visit that has not happened yet.
DELETE FROM emr.care_timeline
 WHERE encounter_id IN (SELECT e.encounter_id FROM emr.encounter e, doc d WHERE e.created_by = d.uid_text)
    OR appointment_id IN (SELECT a.appointment_id FROM emr.appointment a WHERE a.doctor_id IN (SELECT uid FROM doc));
DELETE FROM emr.encounter   WHERE created_by IN (SELECT uid_text FROM doc);
-- The history goes with them. It has no foreign key to the appointment, so deleting the row leaves its
-- snapshots behind — and since the appointment ids are DERIVED from the slot, the re-created appointment
-- inherits them. Left alone, every re-run added another lap to the same timeline: checked in, completed,
-- booked, checked in, completed.
DELETE FROM emr.appointment_history
 WHERE appointment_id IN (SELECT a.appointment_id FROM emr.appointment a WHERE a.doctor_id IN (SELECT uid FROM doc));
DELETE FROM emr.appointment WHERE doctor_id IN (SELECT uid FROM doc);
DELETE FROM emr.appointment_slot s
 WHERE s.doctor_id IN (SELECT uid FROM doc)
   AND NOT EXISTS (SELECT 1 FROM emr.appointment a WHERE a.slot_id = s.slot_id);

-- ── The practitioner ────────────────────────────────────────────────────────────────────────────────────────
-- Internal medicine, which is the specialty that sees the widest range of presentations — a diabetic review,
-- a hypertensive follow-up and a chest infection all belong on one list, so the clinic reads as a real day
-- rather than a set of unrelated rows.
--
-- The licence expires in 2028: this doctor is NOT the one who exercises the licence gate. Dr Omar Adel's
-- lapsed on 2026-07-25 and is deliberately left that way; keep it that way.
INSERT INTO provider.practitioner
  (practitioner_id, tenant_id, user_id, practitioner_type, full_name_en, full_name_ar, license_no, license_expiry, status)
SELECT d.uid, '11111111-1111-1111-1111-111111111111', d.uid_text, 'Doctor',
       'Dr Karim Abdel-Latif', 'د. كريم عبد اللطيف', 'EGMED-118427', DATE '2028-04-30', 'Active'
FROM doc d
ON CONFLICT (practitioner_id) DO UPDATE
  SET user_id = EXCLUDED.user_id, full_name_en = EXCLUDED.full_name_en, full_name_ar = EXCLUDED.full_name_ar,
      license_no = EXCLUDED.license_no, license_expiry = EXCLUDED.license_expiry,
      status = 'Active', is_deleted = false, updated_at = now();

-- Primary specialty is not decoration: a practitioner without one is dropped by the booking picker
-- (bookableDoctors.ts), so they exist in the directory and can never be booked. Endocrinology second, which
-- is what a diabetes clinic is filed under.
INSERT INTO provider.practitioner_specialty (practitioner_id, specialty_code, is_primary)
SELECT d.uid, s.code, s.is_primary FROM doc d,
     (VALUES ('IM', true), ('ENDO', false)) AS s(code, is_primary)
ON CONFLICT (practitioner_id, specialty_code) DO UPDATE SET is_primary = EXCLUDED.is_primary;

-- ── Branch assignments ──────────────────────────────────────────────────────────────────────────────────────
-- TWO branches, because one branch cannot demonstrate branch scoping. Dokki is the home clinic; Maadi is an
-- evening list. Switching the active branch in the portal should change what "My Visits" returns, and with a
-- single assignment there is nothing to switch to.
--
-- These are the PROVIDER-side assignments (who may be rostered where). The IDENTITY-side grants that decide
-- which branch header the token will accept are separate, and seeded just below.
INSERT INTO provider.practitioner_branch_assignment (assignment_id, practitioner_id, branch_id, valid_from, status)
SELECT pg_temp.did('doctor-branch:'||d.uid_text||':'||b.branch_id), d.uid, b.branch_id, DATE '2026-01-01', 'Active'
FROM doc d, (VALUES
    ('0190b100-0000-7000-8000-000000000005'::uuid),   -- DOK — Dokki, the home clinic
    ('0190b100-0000-7000-8000-000000000004'::uuid)    -- MAA — Maadi, the evening list
) AS b(branch_id)
ON CONFLICT (assignment_id) DO UPDATE SET status = 'Active', valid_to = NULL;

-- The identity-side grants. The `doctor` account already had Home = Dokki from the branch-management seed;
-- this makes the pair explicit and survives a reset of that table. Home first — the unique index allows only
-- one active Home per user, so a second one would fail rather than silently move it.
INSERT INTO admin.user_branch_assignment
  (assignment_id, tenant_id, subject_user_id, branch_id, assignment_type, valid_from, status, created_by)
SELECT pg_temp.did('doctor-grant:'||d.uid_text||':'||g.branch_id), '11111111-1111-1111-1111-111111111111',
       d.uid_text, g.branch_id, g.kind, DATE '2026-01-01', 'Active', 'seed:doctor-account'
FROM doc d, (VALUES
    ('0190b100-0000-7000-8000-000000000005'::uuid, 'Home'),
    ('0190b100-0000-7000-8000-000000000004'::uuid, 'Additional')
) AS g(branch_id, kind)
WHERE NOT EXISTS (
    SELECT 1 FROM admin.user_branch_assignment x
    WHERE x.subject_user_id = d.uid_text AND x.branch_id = g.branch_id AND x.status = 'Active')
ON CONFLICT (assignment_id) DO NOTHING;

-- The account's display name, so the portal greets a doctor rather than the word "Doctor".
UPDATE identity."user" SET display_name = 'Dr Karim Abdel-Latif' WHERE user_name = 'doctor';

-- ── Roster ──────────────────────────────────────────────────────────────────────────────────────────────────
-- Saturday–Thursday, closed Friday (dow 5) — the Egyptian working week, matching every other roster here.
-- Dokki is the morning clinic at Cairo Care Clinic's Downtown location, 20-minute consultations; Maadi is a
-- twice-weekly evening list at Nile Central's Main Campus.
INSERT INTO emr.provider_availability
  (availability_id, provider_id, location_id, doctor_id, day_of_week, start_time, end_time, slot_minutes, branch_id, tenant_id)
SELECT pg_temp.did('doctor-roster:'||d.uid_text||':'||r.branch_id||':'||dow),
       r.provider_id, r.location_id, d.uid, dow, r.starts, r.ends, 20, r.branch_id,
       '11111111-1111-1111-1111-111111111111'
-- Explicit CROSS JOINs, not commas: `FROM a, b CROSS JOIN LATERAL c` groups c with b alone, so the lateral
-- could not see `r`. The chained form left-associates and keeps everything to its left in scope.
FROM doc d
CROSS JOIN (VALUES
    ('b0000000-0000-4000-8000-000000000002'::uuid, 'b1000000-0000-4000-8000-000000000003'::uuid,
     '0190b100-0000-7000-8000-000000000005'::uuid, TIME '09:00', TIME '15:00', ARRAY[0,1,2,3,4,6]),
    ('b0000000-0000-4000-8000-000000000001'::uuid, 'b1000000-0000-4000-8000-000000000001'::uuid,
     '0190b100-0000-7000-8000-000000000004'::uuid, TIME '17:00', TIME '20:00', ARRAY[0,3])
) AS r(provider_id, location_id, branch_id, starts, ends, days)
CROSS JOIN LATERAL unnest(r.days) AS w(dow)
ON CONFLICT (availability_id) DO UPDATE
  SET start_time = EXCLUDED.start_time, end_time = EXCLUDED.end_time, slot_minutes = EXCLUDED.slot_minutes;

-- ── Slots: two weeks back, two weeks forward ────────────────────────────────────────────────────────────────
-- Backwards as well as forwards because a doctor portal with no history has no patient list: "My Patients"
-- reads past encounters, and an encounter needs an appointment to have been started from.
--
-- `AT TIME ZONE 'Africa/Cairo'` and not a fixed +02:00 — Egypt observes DST again, so a hardcoded offset puts
-- every summer slot an hour off the wall clock the roster was written in.
INSERT INTO emr.appointment_slot (slot_id, provider_id, location_id, doctor_id, slot_start, slot_end, branch_id, tenant_id)
SELECT pg_temp.did('doctor-slot:'||a.availability_id||':'||day||':'||mins),
       a.provider_id, a.location_id, a.doctor_id,
       (day + a.start_time) AT TIME ZONE 'Africa/Cairo' + (mins || ' minutes')::interval,
       (day + a.start_time) AT TIME ZONE 'Africa/Cairo' + ((mins + a.slot_minutes) || ' minutes')::interval,
       a.branch_id, a.tenant_id
FROM emr.provider_availability a
CROSS JOIN doc d
CROSS JOIN LATERAL (SELECT (CURRENT_DATE + offs)::date AS day FROM generate_series(-14, 14) AS offs) dd
CROSS JOIN LATERAL (
    SELECT gs AS mins FROM generate_series(
        0, (extract(epoch FROM (a.end_time - a.start_time)) / 60)::int - a.slot_minutes, a.slot_minutes) gs
) tt
WHERE a.doctor_id = d.uid
  AND extract(dow FROM day) = a.day_of_week
ON CONFLICT (slot_id) DO NOTHING;

-- ── Today's clinic ──────────────────────────────────────────────────────────────────────────────────────────
-- Positioned relative to now(), not to a fixed hour, so the board looks like a clinic in progress whenever the
-- file is run: the three most recent slots are patients already checked in and waiting to be called, the ones
-- after that are still to come. "Start visit" is enabled only on a CheckedIn row, so without those three the
-- portal's central action cannot be reached at all.
--
-- Ten of eighteen slots are taken. The gaps are deliberate: a clinic with no free times cannot be used to
-- test booking, and reception books into this same calendar.
CREATE TEMP TABLE today_clinic ON COMMIT DROP AS
WITH slots AS (
    SELECT s.*,
           -- Nearest-first, past before future: rank 1..3 are the arrivals, the rest are the upcoming list.
           row_number() OVER (ORDER BY (s.slot_start > now()),
                                       CASE WHEN s.slot_start <= now()
                                            THEN -extract(epoch FROM s.slot_start)
                                            ELSE  extract(epoch FROM s.slot_start) END) AS rn
    FROM emr.appointment_slot s, doc d
    WHERE s.doctor_id = d.uid
      AND s.branch_id = '0190b100-0000-7000-8000-000000000005'
      AND (s.slot_start AT TIME ZONE 'Africa/Cairo')::date = (now() AT TIME ZONE 'Africa/Cairo')::date
)
SELECT s.*, p.member_no, p.status AS appt_status
FROM slots s
JOIN (VALUES
    -- Waiting now — the three the doctor is about to call in.
    (1,  'MRS-M-2026-000004', 'CheckedIn'),   -- Fatma Ibrahim
    (2,  'MRS-M-2026-000009', 'CheckedIn'),   -- Khaled Mostafa
    (3,  'MRS-M-2026-000019', 'CheckedIn'),   -- Layla Haddad
    -- Still to come today.
    (4,  'MRS-M-2026-000010', 'Booked'),      -- Salma Gamal
    (5,  'MRS-M-2026-000008', 'Booked'),      -- Nour El-Sayed
    (7,  'MRS-M-2026-000020', 'Booked'),      -- Bassam Kanaan
    (8,  'MRS-M-2026-000006', 'Booked'),      -- Mariam Saleh
    (11, 'MRS-M-2026-000015', 'Booked'),      -- Hassan Awad
    (13, 'MRS-M-2026-000003', 'Booked'),      -- Omar Khalil
    (14, 'MRS-M-2026-000021', 'Booked')       -- Mostafa Younis
) AS p(rn, member_no, status) ON p.rn = s.rn;

-- ── History: the last two weeks of the Dokki clinic ─────────────────────────────────────────────────────────
-- Completed visits, one no-show and one cancellation. Every third slot, so past days look attended rather
-- than fully booked, and the patient is chosen deterministically from the slot id — the same patient comes
-- back to the same recurring slot, which is what a follow-up list actually looks like.
CREATE TEMP TABLE past_clinic ON COMMIT DROP AS
SELECT s.slot_id, s.provider_id, s.location_id, s.doctor_id, s.slot_start, s.slot_end, s.branch_id, s.tenant_id,
       b.beneficiary_id, b.given_name || ' ' || b.family_name AS beneficiary_name,
       CASE WHEN s.rn % 13 = 0 THEN 'NoShow'
            WHEN s.rn % 17 = 0 THEN 'Cancelled'
            ELSE 'Completed' END AS appt_status,
       s.rn
FROM (
    SELECT s.*, row_number() OVER (ORDER BY s.slot_start) AS rn
    FROM emr.appointment_slot s, doc d
    WHERE s.doctor_id = d.uid
      AND s.slot_end < date_trunc('day', now() AT TIME ZONE 'Africa/Cairo') AT TIME ZONE 'Africa/Cairo'
) s
JOIN LATERAL (
    SELECT bn.* FROM patient.beneficiary bn
    WHERE bn.status = 'Active'
    ORDER BY md5(bn.beneficiary_id::text || s.slot_id::text)
    LIMIT 1
) b ON true
WHERE s.rn % 3 = 0;

-- ── Future: the rest of the fortnight, lightly booked ───────────────────────────────────────────────────────
-- Follow-ups the doctor has already given out. Sparse on purpose — the same free-slot argument as today.
--
-- Runs from now() rather than from tomorrow so it also fills tonight's MAADI list. Without that, switching the
-- active branch to Maadi shows an empty board today and the branch-scoping narrowing looks broken when it is
-- working perfectly. Today's Dokki slots are excluded because today_clinic already claimed them.
CREATE TEMP TABLE future_clinic ON COMMIT DROP AS
SELECT s.slot_id, s.provider_id, s.location_id, s.doctor_id, s.slot_start, s.slot_end, s.branch_id, s.tenant_id,
       b.beneficiary_id, b.given_name || ' ' || b.family_name AS beneficiary_name
FROM (
    SELECT s.*, row_number() OVER (ORDER BY s.slot_start) AS rn
    FROM emr.appointment_slot s, doc d
    WHERE s.doctor_id = d.uid
      AND s.slot_start > now()
      AND s.slot_id NOT IN (SELECT slot_id FROM today_clinic)
) s
JOIN LATERAL (
    SELECT bn.* FROM patient.beneficiary bn
    WHERE bn.status = 'Active'
    ORDER BY md5(bn.beneficiary_id::text || 'f' || s.slot_id::text)
    LIMIT 1
) b ON true
WHERE s.rn % 7 = 0;

-- ── The board, planned before it is written ─────────────────────────────────────────────────────────────────
-- Materialised rather than inserted straight, because these rows are not written in their final state: every
-- appointment is BOOKED first and then walked to where it belongs. See the walk below for why.
CREATE TEMP TABLE appt_plan ON COMMIT DROP AS
SELECT pg_temp.did('doctor-appt:'||c.slot_id) AS appointment_id, c.*
FROM (
    SELECT t.slot_id, t.provider_id, t.location_id, t.slot_start, t.slot_end, t.branch_id, t.tenant_id,
           t.doctor_id, b.beneficiary_id, b.given_name || ' ' || b.family_name AS beneficiary_name,
           t.appt_status, 'Scheduled'::text AS appointment_type
      FROM today_clinic t JOIN patient.beneficiary b ON b.member_no = t.member_no
    UNION ALL
    SELECT p.slot_id, p.provider_id, p.location_id, p.slot_start, p.slot_end, p.branch_id, p.tenant_id,
           p.doctor_id, p.beneficiary_id, p.beneficiary_name, p.appt_status, 'Scheduled'
      FROM past_clinic p
    UNION ALL
    -- `Scheduled`, not `FollowUp`, even though clinically these are follow-ups: appointment_type = 'FollowUp'
    -- carries a row CHECK that it names the encounter it came from, and that check fires on INSERT — a
    -- post-hoc UPDATE would never get the chance to run. Correct answer is the honest type, not a weakened
    -- constraint.
    SELECT f.slot_id, f.provider_id, f.location_id, f.slot_start, f.slot_end, f.branch_id, f.tenant_id,
           f.doctor_id, f.beneficiary_id, f.beneficiary_name, 'Booked', 'Scheduled'
      FROM future_clinic f
) c;

INSERT INTO emr.appointment
  (appointment_id, beneficiary_id, provider_id, location_id, slot_id, appointment_type, status,
   scheduled_start, scheduled_end, branch_id, tenant_id, doctor_id, beneficiary_name, created_by, created_at)
SELECT p.appointment_id, p.beneficiary_id, p.provider_id, p.location_id, p.slot_id,
       p.appointment_type, 'Booked', p.slot_start, p.slot_end, p.branch_id, p.tenant_id, p.doctor_id,
       p.beneficiary_name, 'seed:doctor-account', now()
FROM appt_plan p
-- Untargeted: now that every row starts Booked it also has to clear `ux_appointment_active_slot`, and a slot
-- may already hold an appointment written under another seed's id. A seed skips; it does not abort.
ON CONFLICT DO NOTHING;

-- ── Walk each appointment to where it belongs ───────────────────────────────────────────────────────────────
-- Every appointment goes through Booked. Writing the final status straight into the INSERT skipped that, and
-- the consequence was not cosmetic: `emr.appointment_history` is filled by a row trigger, so a row inserted
-- as CheckedIn has exactly one history row and its timeline OPENS at check-in. A desk reading the episode of
-- a patient who is standing in front of them saw a visit that began mid-story — no booking, no answer to
-- "when was this arranged, and by whom?", which is the question a timeline is opened for as often as any.
--
-- So: insert Booked, then transition. Each UPDATE fires the trigger and lays down the step it represents.
-- Guarded on the CURRENT status so a re-run over rows that already exist is a no-op rather than a second
-- lap, and so a NoShow never passes through CheckedIn — the whole point of a no-show is that they did not
-- arrive, and a timeline claiming otherwise is worse than no timeline.
UPDATE emr.appointment a SET status = 'CheckedIn', updated_by = (SELECT uid_text FROM desk), updated_at = now()
  FROM appt_plan p
 WHERE a.appointment_id = p.appointment_id AND a.status = 'Booked'
   AND p.appt_status IN ('CheckedIn', 'Completed');

UPDATE emr.appointment a SET status = 'Completed', updated_by = (SELECT uid_text FROM doc), updated_at = now()
  FROM appt_plan p
 WHERE a.appointment_id = p.appointment_id AND a.status = 'CheckedIn'
   AND p.appt_status = 'Completed';

UPDATE emr.appointment a SET status = p.appt_status, no_show = (p.appt_status = 'NoShow'),
       cancel_reason = CASE WHEN p.appt_status = 'Cancelled' THEN 'PatientRequest' END,
       updated_by = (SELECT uid_text FROM desk), updated_at = now()
  FROM appt_plan p
 WHERE a.appointment_id = p.appointment_id AND a.status = 'Booked'
   AND p.appt_status IN ('NoShow', 'Cancelled');

-- ── Plausible times on those steps ──────────────────────────────────────────────────────────────────────────
-- The trigger stamps `changed_at` with the transaction clock, so the walk above would file a booking, an
-- arrival and a completed visit at the same second — a timeline that is complete and reads as nonsense. The
-- appointments themselves are invented; their history is invented with them, and to the same standard.
--
-- A booking is always in the past (LEAST, so a slot two weeks out is not "booked" a week from now); arrival
-- is a few minutes before the slot; the visit ends inside the half-hour it was given.
UPDATE emr.appointment_history h
   SET changed_at = CASE h.row_snapshot ->> 'status'
         WHEN 'Booked'    THEN LEAST(now() - interval '2 days', a.scheduled_start - interval '3 days')
         WHEN 'CheckedIn' THEN a.scheduled_start - interval '9 minutes'
         WHEN 'Completed' THEN a.scheduled_start + interval '26 minutes'
         WHEN 'NoShow'    THEN a.scheduled_start + interval '20 minutes'
         WHEN 'Cancelled' THEN LEAST(now() - interval '1 hour', a.scheduled_start - interval '1 day')
         ELSE h.changed_at
       END
  FROM emr.appointment a
 WHERE a.appointment_id = h.appointment_id
   AND a.created_by = 'seed:doctor-account';

-- ── Encounters for the completed visits ─────────────────────────────────────────────────────────────────────
-- `created_by` is the doctor's user id because that is the column GET /encounters/mine filters on — the
-- "My Patients" panel is literally "encounters I opened". Written with a signed SOAP note, a coded diagnosis
-- and vitals, so opening one from the patient list shows a record rather than an empty shell.
CREATE TEMP TABLE seeded_encounters ON COMMIT DROP AS
SELECT pg_temp.did('doctor-enc:'||a.appointment_id) AS encounter_id,
       a.appointment_id, a.beneficiary_id, a.provider_id, a.scheduled_start,
       row_number() OVER (ORDER BY a.scheduled_start) AS rn
FROM emr.appointment a, doc d
WHERE a.doctor_id = d.uid AND a.status = 'Completed' AND a.created_by = 'seed:doctor-account';

-- Encounter numbers continue the platform's own sequence rather than restarting it, and the sequence is moved
-- on afterwards so the next visit started through the UI does not collide with these.
INSERT INTO emr.encounter
  (encounter_id, encounter_no, beneficiary_id, appointment_id, provider_id, status, started_at, created_by, tenant_id)
SELECT e.encounter_id,
       'ENC-2026-' || lpad((COALESCE((SELECT last_value FROM emr.encounter_seq WHERE year = 2026), 0) + e.rn)::text, 6, '0'),
       e.beneficiary_id, e.appointment_id, e.provider_id, 'Completed', e.scheduled_start, d.uid_text,
       '11111111-1111-1111-1111-111111111111'
FROM seeded_encounters e, doc d
ON CONFLICT (encounter_id) DO NOTHING;

INSERT INTO emr.encounter_seq (year, last_value)
SELECT 2026, (SELECT count(*) FROM seeded_encounters)
ON CONFLICT (year) DO UPDATE
  SET last_value = emr.encounter_seq.last_value + (SELECT count(*) FROM seeded_encounters);

-- Six presentations an internal-medicine list actually contains, cycled across the encounters. ICD-10 codes
-- are real codes for the conditions named; the patients are not.
INSERT INTO emr.emr_note
  (note_id, encounter_id, note_type, subjective, objective, assessment, plan, authored_by, authored_at, is_signed, signed_at, tenant_id)
SELECT pg_temp.did('doctor-note:'||e.encounter_id), e.encounter_id, 'SOAP',
       n.subjective, n.objective, n.assessment, n.plan,
       d.uid_text, e.scheduled_start + interval '25 minutes', true, e.scheduled_start + interval '30 minutes',
       '11111111-1111-1111-1111-111111111111'
FROM seeded_encounters e
CROSS JOIN doc d
JOIN LATERAL (
    SELECT * FROM (VALUES
      (0, 'Routine three-month diabetes review. Taking metformin 1g twice daily, no hypoglycaemic episodes. Reports occasional numbness in both feet.',
          'BP 138/84. Weight stable. Feet: intact pulses, reduced monofilament sensation over both great toes.',
          'Type 2 diabetes, reasonably controlled. Early peripheral neuropathy.',
          'Continue metformin. HbA1c and lipid panel today. Podiatry referral. Review in three months.'),
      (1, 'Follow-up for hypertension. Compliant with amlodipine 5mg. No headaches, no chest pain.',
          'BP 132/80 seated, repeated 130/78. Heart sounds normal, chest clear, no ankle oedema.',
          'Essential hypertension, at target on current dose.',
          'Continue amlodipine 5mg daily. Renal function and electrolytes in six months. Review in three months.'),
      (2, 'Four days of productive cough with green sputum and fever. No shortness of breath at rest.',
          'Temp 38.1. RR 18, SpO2 97% on air. Coarse crackles at the right base.',
          'Community-acquired lower respiratory tract infection.',
          'Amoxicillin 500mg three times daily for five days. Fluids and rest. Return if breathless or fever persists beyond 48 hours.'),
      (3, 'Tiredness and heavier periods over the last few months. Diet low in red meat.',
          'Conjunctival pallor. No hepatosplenomegaly. Pulse 88 regular.',
          'Suspected iron-deficiency anaemia.',
          'Full blood count and ferritin today. Start ferrous sulfate 200mg daily. Review with results in two weeks.'),
      (4, 'Epigastric burning after meals for six weeks, worse at night. No weight loss, no vomiting.',
          'Soft abdomen, mild epigastric tenderness, no guarding or masses.',
          'Dyspepsia, likely gastro-oesophageal reflux.',
          'Omeprazole 20mg daily for four weeks. H. pylori stool antigen. Avoid late meals. Review in one month.'),
      (5, 'Annual review, feels well. No complaints. Family history of ischaemic heart disease.',
          'BP 126/78. BMI 27.4. Cardiovascular and respiratory examination unremarkable.',
          'Well adult with cardiovascular risk factors.',
          'Fasting lipids and glucose. Weight and activity advice given. Review in twelve months.')
    ) AS v(slot, subjective, objective, assessment, plan)
    WHERE v.slot = e.rn % 6
) n ON true
ON CONFLICT (note_id) DO NOTHING;

INSERT INTO emr.diagnosis (diagnosis_id, encounter_id, icd_code, diagnosis_rank, clinical_status, recorded_by, recorded_at, tenant_id)
SELECT pg_temp.did('doctor-dx:'||e.encounter_id), e.encounter_id, x.icd, 'Primary', x.state,
       d.uid_text, e.scheduled_start + interval '25 minutes', '11111111-1111-1111-1111-111111111111'
FROM seeded_encounters e
CROSS JOIN doc d
JOIN LATERAL (
    SELECT * FROM (VALUES
      (0, 'E11.9',  'Active'),    -- Type 2 diabetes without complications
      (1, 'I10',    'Active'),    -- Essential hypertension
      (2, 'J22',    'Resolved'),  -- Acute lower respiratory infection
      (3, 'D50.9',  'Active'),    -- Iron-deficiency anaemia
      (4, 'K21.9',  'Active'),    -- GORD without oesophagitis
      (5, 'Z00.0',  'Resolved')   -- General adult medical examination
    ) AS v(slot, icd, state)
    WHERE v.slot = e.rn % 6
) x ON true
ON CONFLICT (diagnosis_id) DO NOTHING;

-- Vitals vary per encounter rather than repeating one row, because a flat series is the giveaway that a chart
-- is fixture data — and a chart is where a doctor looks for a trend.
INSERT INTO emr.vital (vital_id, encounter_id, vital_type, value_num, unit, recorded_by, measured_at, tenant_id)
SELECT pg_temp.did('doctor-vital:'||e.encounter_id||':'||v.vital_type), e.encounter_id, v.vital_type,
       v.base + ((e.rn * v.step) % v.spread), v.unit,
       d.uid_text, e.scheduled_start + interval '5 minutes', '11111111-1111-1111-1111-111111111111'
FROM seeded_encounters e, doc d,
     (VALUES ('HR', 66, 3, 22, 'bpm'), ('Temp', 36.4, 0.2, 1.2, 'C'),
             ('SpO2', 96, 1, 4, '%'),  ('Weight', 64, 2, 26, 'kg')
     ) AS v(vital_type, base, step, spread, unit)
ON CONFLICT (vital_id) DO NOTHING;

COMMIT;

-- ── What was created ────────────────────────────────────────────────────────────────────────────────────────
\echo ''
\echo '── The doctor ─────────────────────────────────────────────────────────────'
SELECT u.user_name AS login, p.full_name_en AS name, p.practitioner_id,
       (SELECT string_agg(s.specialty_code || CASE WHEN s.is_primary THEN '*' ELSE '' END, ', ' ORDER BY s.is_primary DESC)
          FROM provider.practitioner_specialty s WHERE s.practitioner_id = p.practitioner_id) AS specialties,
       (SELECT string_agg(b.branch_code, ', ' ORDER BY b.branch_code)
          FROM provider.practitioner_branch_assignment a
          JOIN provider.branch b ON b.branch_id = a.branch_id
         WHERE a.practitioner_id = p.practitioner_id AND a.status = 'Active') AS branches,
       p.license_no, p.license_expiry
FROM provider.practitioner p
JOIN identity."user" u ON u.id = p.practitioner_id
WHERE u.user_name = 'doctor';

\echo ''
\echo '── Their appointments ─────────────────────────────────────────────────────'
SELECT a.status,
       count(*) FILTER (WHERE (a.scheduled_start AT TIME ZONE 'Africa/Cairo')::date
                            = (now() AT TIME ZONE 'Africa/Cairo')::date) AS today,
       count(*) AS total
FROM emr.appointment a
JOIN identity."user" u ON u.id = a.doctor_id AND u.user_name = 'doctor'
GROUP BY a.status ORDER BY a.status;

\echo ''
\echo '── Clinical history ───────────────────────────────────────────────────────'
SELECT 'emr.encounter' AS t, count(*) FROM emr.encounter e
  JOIN identity."user" u ON u.id::text = e.created_by AND u.user_name = 'doctor'
UNION ALL SELECT 'emr.emr_note',  count(*) FROM emr.emr_note n
  JOIN identity."user" u ON u.id::text = n.authored_by AND u.user_name = 'doctor'
UNION ALL SELECT 'emr.diagnosis', count(*) FROM emr.diagnosis x
  JOIN identity."user" u ON u.id::text = x.recorded_by AND u.user_name = 'doctor'
UNION ALL SELECT 'emr.vital',     count(*) FROM emr.vital v
  JOIN identity."user" u ON u.id::text = v.recorded_by AND u.user_name = 'doctor'
UNION ALL SELECT 'emr.appointment_slot (free, future)', count(*) FROM emr.appointment_slot s
  JOIN identity."user" u ON u.id = s.doctor_id AND u.user_name = 'doctor'
 WHERE s.slot_start > now()
   AND NOT EXISTS (SELECT 1 FROM emr.appointment a
                    WHERE a.slot_id = s.slot_id AND a.status IN ('Booked','CheckedIn'))
ORDER BY 1;
