-- eligibility-service — 0006 the benefit category is a CODE, and the column now says so.
--
-- ============================================================================================================
-- WHAT WAS WRONG
-- ============================================================================================================
-- `coverage_projection.benefit_category` is matched against the canonical, CLOSED vocabulary in
-- 22-data-dictionary §11 — CONSULT / LAB / IMAGING / PHARMACY / REFERRAL — which is what every caller sends
-- to POST /eligibility/check and what policy-service publishes on CoverageChanged.
--
-- The seeds wrote something else. Three of them, inventing three vocabularies between them:
--
--   tools/dev/seed-dev-clinic.sql   SELECT bc.name   → 'Consultation', 'Laboratory', 'Imaging', 'Pharmacy'
--   infra/compose/seed/reception_seed.sql            → 'Outpatient', 'Labs', 'Pharmacy'
--   infra/compose/seed/case_seed.sql                 → 'Oncology', 'Chronic', 'Outpatient'
--
-- So the engine answered "no active coverage for LAB" to members who hold laboratory cover, and the same for
-- CONSULT and IMAGING. PHARMACY was the one category that happened to work, and only because 'Pharmacy'
-- matches 'PHARMACY' case-insensitively — which is why the defect surfaced as "the price will not show" on a
-- dispensing screen rather than as the much larger thing it is: an eligibility engine telling a refugee
-- beneficiary they are not covered for a test they are covered for.
--
-- Nobody noticed because the screens fed by these seeds render the string. A display field shows whatever it
-- is given; a MATCHED field does not, and this is a matched field.
--
-- ============================================================================================================
-- THE MAPPING, AND THE ONE PLACE IT IS A JUDGEMENT
-- ============================================================================================================
-- 'Consultation' / 'Outpatient' → CONSULT, 'Laboratory' / 'Labs' → LAB, 'Imaging' → IMAGING,
-- 'Pharmacy' → PHARMACY. Those are the same categories under their display names, so the mapping is a
-- rename, not a decision.
--
-- 'Oncology' and 'Chronic' are NOT. The canonical set has no counterpart for either — they are illustrative
-- programme names a case-management fixture invented. They map to CONSULT because that is the category the
-- care they describe is actually delivered under, and the alternative (leaving them, or minting categories to
-- match a fixture) is how a closed vocabulary stops being closed. The case screens will show CONSULT where
-- they showed 'Oncology'; that is a fixture losing a label it should never have carried in this column.

UPDATE eligibility.coverage_projection SET benefit_category = CASE benefit_category
    WHEN 'Consultation' THEN 'CONSULT'
    WHEN 'Outpatient'   THEN 'CONSULT'
    WHEN 'Oncology'     THEN 'CONSULT'
    WHEN 'Chronic'      THEN 'CONSULT'
    WHEN 'Laboratory'   THEN 'LAB'
    WHEN 'Labs'         THEN 'LAB'
    WHEN 'Imaging'      THEN 'IMAGING'
    WHEN 'Pharmacy'     THEN 'PHARMACY'
    WHEN 'Referral'     THEN 'REFERRAL'
    ELSE upper(benefit_category)
END
WHERE benefit_category <> upper(benefit_category)
   OR benefit_category IN ('Outpatient','Labs','Oncology','Chronic');

-- ============================================================================================================
-- AND THE COLUMN NOW REFUSES THE NEXT ONE
-- ============================================================================================================
-- The vocabulary is closed, so the column says so. Without this the fix lasts exactly until the next seed or
-- the next publisher writes a display name into it, and the failure mode is silent: a category nothing
-- matches reads as a member with no cover, which is indistinguishable from a member who genuinely has none.
--
-- '' is admitted because it is the column's own default and the projector writes it when a CoverageChanged
-- carries no category — a gap worth seeing as a gap rather than one worth crashing the consumer over.
ALTER TABLE eligibility.coverage_projection DROP CONSTRAINT IF EXISTS ck_coverage_projection_category;  -- migrate-compat: contract-ok (idempotent re-run guard for a constraint this same migration creates below; it has never existed on a previously-deployed build)
ALTER TABLE eligibility.coverage_projection
    ADD CONSTRAINT ck_coverage_projection_category
    CHECK (benefit_category IN ('', 'CONSULT', 'LAB', 'IMAGING', 'PHARMACY', 'REFERRAL'));

COMMENT ON COLUMN eligibility.coverage_projection.benefit_category IS
    'The canonical benefit-category CODE (22-data-dictionary §11), never a display name. Matched against '
    'what callers send to /eligibility/check; a value outside the set reads as "no cover" rather than as a '
    'bad row, which is why the CHECK exists.';
