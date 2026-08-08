-- ===========================================================================================================
-- DEV ONLY — backfill the display names on prescriptions written before the snapshot existed.
-- ===========================================================================================================
--
-- Pharmacy migration 0006 added `prescription.prescriber_name` and `prescription_line.drug_name`, and the
-- submit path has populated them since. Every prescription written BEFORE that deploy carries only a drug
-- uuid and a prescriber uuid, so the encounter's read-back dialog and the dispensing counter both say
-- "Medication not recorded" / "Prescriber not recorded" — correctly. The data genuinely is not there.
--
-- This fills those gaps for the dev/demo dataset by resolving each row's EXISTING references: the drug_id
-- against the master catalogue, the prescriber_id against the identity directory. Nothing is invented; the
-- ids were always in the row.
--
-- ------------------------------------------------------------------------------------------------------
-- WHY THIS IS NOT A MIGRATION
-- ------------------------------------------------------------------------------------------------------
-- Two reasons, and both matter more in production than they do here.
--
-- 1. It reads across service boundaries. `masterdata.drug` and `identity."user"` belong to other services;
--    pharmacy reaches them over HTTP with a caller's token, never by joining their tables. A migration that
--    did this would work only because the dev deployment happens to put every schema in one database, and
--    would break the moment a service is moved — while making the coupling invisible.
--
-- 2. It changes what the column MEANS. `drug_name` is a snapshot: what the prescriber actually selected, at
--    the moment they selected it. A backfill writes what the catalogue says TODAY. Those are the same string
--    right up until a product is renamed, reformulated or withdrawn — and the whole reason to snapshot is
--    the day they differ. Running this in production would quietly relabel history with no marker saying so.
--
-- Here the prescriptions are days old and the catalogue has not moved, so the two are identical and the
-- demo stops looking broken. That is the entire justification, and it does not travel.
--
-- Usage:
--   docker compose exec -T postgres psql -U hbmp -d hbmp -f - < tools/dev/backfill-rx-display-names.sql
-- ===========================================================================================================

BEGIN;

-- Same shape the write path composes (Api/HttpClients.cs, HttpDrugValidator): trade name, then strength,
-- then form. "Augmentin" alone does not tell a pharmacist whether to reach for 375mg or 1g, and the dose
-- field beside it is the PRESCRIBED dose, not the product's.
UPDATE pharmacy.prescription_line AS l
   SET drug_name = concat_ws(' ',
         nullif(btrim(d.name), ''),
         nullif(btrim(d.strength), ''),
         nullif(btrim(d.form), ''))
  FROM masterdata.drug AS d
 WHERE d.drug_id = l.drug_id
   AND l.drug_name IS NULL;

-- The prescriber is resolved from prescriber_id, which migration 0006 already corrected from the zero guid.
-- Rows still holding the zero guid are left alone: there is nothing to resolve, and a NULL that says so is
-- better than a name attached to the wrong person.
UPDATE pharmacy.prescription AS p
   SET prescriber_name = u.display_name
  FROM identity."user" AS u
 WHERE u.id = p.prescriber_id
   AND p.prescriber_name IS NULL
   AND nullif(btrim(u.display_name), '') IS NOT NULL;

COMMIT;

-- What is left unresolved, and therefore still shown as "not recorded" — an empty result is the goal.
SELECT p.rx_no, l.drug_id, p.prescriber_id
  FROM pharmacy.prescription p
  JOIN pharmacy.prescription_line l ON l.prescription_id = p.prescription_id
 WHERE l.drug_name IS NULL OR p.prescriber_name IS NULL
 ORDER BY p.submitted_at;
