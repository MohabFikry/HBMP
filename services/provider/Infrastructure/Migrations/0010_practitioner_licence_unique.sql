-- provider-service — 0010: one practitioner identity, many branch assignments.
-- Phase 25.2 · design 42 §2 · ADR-0029 (D3). ADDITIVE: adds one index, changes no data.
--
-- WHY
-- ---
-- A doctor working at Maadi and Dokki must be ONE practitioner row with two assignments, not two records.
-- Without that rule you get three "Dr Hala Fouad" rows and a roster nobody can reason about: which of them
-- holds the current licence, which the appointments point at, which to suspend when the licence lapses.
--
-- D3 lets a branch coordinator CREATE a practitioner rather than raising a ticket to head office for every
-- locum — which means the duplicate is now something six clinics can produce independently, each in good
-- faith, none able to see the others' roster. The licence number is the one identifier that is externally
-- issued, unique by construction, and already on the table. This index is the cheapest defence there is.
--
-- WHERE IT IS ENFORCED
-- --------------------
-- At the DATABASE, not only at POST /practitioners. "The endpoint returns 409" is not an invariant a repair
-- script, a data load or a psql session respects — the same reasoning as 19.1's plan-version triggers.
--
-- PARTIAL, on two conditions:
--   is_deleted = false      a soft-deleted practitioner must not block re-registering their licence
--   license_no IS NOT NULL  a nurse recorded without a licence number is not a duplicate of another one
--
-- BACKFILL CHECK — RUN BEFORE APPLYING, and it is not decorative. Merging two clinical identities is a
-- DATA decision (which appointments, which encounters, which specialties survive), never a migration side
-- effect, so this refuses to run rather than choosing for you:
--
--   SELECT license_no, count(*), array_agg(practitioner_id)
--   FROM provider.practitioner
--   WHERE is_deleted = false AND license_no IS NOT NULL
--   GROUP BY license_no HAVING count(*) > 1;
--
-- Verified empty on the dev database at 2026-07-31 (4 practitioners, 0 duplicate licences). The DO block
-- below repeats the check at apply time and aborts with the offending licence numbers, because the
-- environment this runs against next is not the one it was authored against.

DO $$
DECLARE
    dupes text;
BEGIN
    SELECT string_agg(format('%s (%s rows)', license_no, n), ', ')
    INTO dupes
    FROM (
        SELECT license_no, count(*) AS n
        FROM provider.practitioner
        WHERE is_deleted = false AND license_no IS NOT NULL
        GROUP BY license_no
        HAVING count(*) > 1
    ) d;

    IF dupes IS NOT NULL THEN
        RAISE EXCEPTION
            'provider 0010: duplicate licence numbers exist and must be merged by hand first: %', dupes
            USING HINT = 'Merging two clinical identities is a data decision — decide which row survives, '
                         'repoint its assignments/specialties, soft-delete the other, then re-run.';
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_practitioner_license_no
    ON provider.practitioner (license_no)
    WHERE is_deleted = false AND license_no IS NOT NULL;

-- Supports the 409 lookup (find the existing holder of a licence so the UI can offer "assign them to my
-- clinic instead") and the licence-expiry sweeper's "expiring within N days" scan (25.3).
CREATE INDEX IF NOT EXISTS ix_practitioner_license_expiry
    ON provider.practitioner (license_expiry)
    WHERE is_deleted = false AND license_expiry IS NOT NULL;
