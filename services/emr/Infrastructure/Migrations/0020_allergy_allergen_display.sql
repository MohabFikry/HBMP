-- emr-service — 0020: record WHICH substance an allergy is to, in words.
--
-- emr.allergy has always stored allergen_id, a masterdata uuid, and nothing else identifying. Every consumer
-- that needed to SHOW the allergy had no name to show: profile-service's alerts provider reads
-- `allergenDisplay` off this record (ClinicalProviders.cs) and falls back to the raw uuid, so the patient
-- context bar — the strip whose entire job is telling a clinician who and what is in front of them — was one
-- recorded allergy away from rendering "Allergy to 4f2b8c1a-…". A safety control that displays a uuid is a
-- safety control a clinician learns to ignore.
--
-- The name is SNAPSHOT at write time rather than joined at read time, for two reasons:
--   1. This is on the hot path. The context bar loads on every clinical screen against a p95 < 400ms budget
--      (design 39 §6). A fan-out to masterdata per allergy per screen buys nothing at that price.
--   2. It is the more honest record. What belongs in a clinical record is the substance the clinician
--      selected and saw at the moment they recorded it, not whatever masterdata renames that row to later.
--
-- Nullable, deliberately: rows written before this migration have no captured name, and NULL says exactly
-- that. Defaulting them to '' or to the uuid would manufacture a display value nobody ever chose — readers
-- fall back and say "(unspecified)", which is true.
ALTER TABLE emr.allergy ADD COLUMN IF NOT EXISTS allergen_display varchar(160);

COMMENT ON COLUMN emr.allergy.allergen_display IS
    'Allergen name as resolved from masterdata at the moment of recording. NULL for rows written before 0020.';
