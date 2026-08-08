-- masterdata-service — 0013 indication-keyed dosing rules (44-clinical-validation-hardening §4,
-- phase 28 Gate 10). Idempotent.
--
-- ============================================================================================================
-- WHAT THE OLD SHAPE COULD NOT SAY
-- ============================================================================================================
-- `DosingRuleFact(DrugId, MaxDailyDose, DoseUnit, MaxDurationDays)` is an adult, fixed-dose, one-indication
-- check, and its fetcher was a hard-coded empty dictionary. Three dimensions were missing and each of them
-- changes the number:
--
--   INDICATION — the same molecule is dosed differently for different conditions. Amoxicillin for otitis
--                media is not amoxicillin for endocarditis prophylaxis.
--   POPULATION — this clinic's population skews paediatric, and mg/kg is the only correct paediatric
--                calculation. An adult ceiling applied to a four-year-old is not a conservative check; it is
--                no check at all.
--   ROUTE      — oral and intravenous ceilings differ for the same drug.
--
-- KEYED ON THE MOLECULE, like every other clinical rule since 28.1: a rule per PRODUCT would need one row
-- per brand of paracetamol in a 22,653-product catalogue.

CREATE TABLE IF NOT EXISTS masterdata.dosing_rule (
    rule_id          uuid PRIMARY KEY,

    subject_kind     text NOT NULL CHECK (subject_kind IN ('Ingredient','AtcClass')),
    subject_value    text NOT NULL,

    -- NULL means "any indication" — the general ceiling. A rule with a scope is more specific and wins.
    indication_icd_scope text,

    population       text NOT NULL
                     CHECK (population IN ('Neonate','Infant','Child','Adolescent','Adult','Geriatric')),
    route            text,

    dose_unit        text NOT NULL,
    min_single       numeric(12,3),
    max_single       numeric(12,3),
    typical_daily    numeric(12,3),
    max_daily        numeric(12,3),
    max_duration_days int,

    -- mg/kg. `weight_capped_at_adult_dose` matters more than it looks: a 60kg twelve-year-old on a mg/kg
    -- rule can compute past the adult maximum, and a check that reported that as within-range would be
    -- endorsing an overdose it had calculated itself.
    is_weight_based  boolean NOT NULL DEFAULT false,
    mg_per_kg_min    numeric(12,3),
    mg_per_kg_max    numeric(12,3),
    weight_capped_at_adult_dose boolean NOT NULL DEFAULT true,

    -- Set where the drug is renally cleared. The engine reports that eGFR is unavailable rather than dosing
    -- around it — laboratory results are stored as free text, so no structured value exists to adjust by.
    requires_renal_function boolean NOT NULL DEFAULT false,
    renal_adjustment_note text,
    hepatic_note     text,

    citation         text NOT NULL,
    source           text NOT NULL,
    source_release   varchar(64),
    reviewed_by      text,
    reviewed_at      timestamptz,
    is_active        boolean NOT NULL DEFAULT false,

    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),

    -- Same governance rule as every other clinical table in this phase.
    CONSTRAINT ck_dosing_rule_reviewed
        CHECK (NOT is_active OR (reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL)),

    -- A weight-based rule without a mg/kg range is a rule that cannot compute anything.
    CONSTRAINT ck_dosing_rule_weight_based_has_range
        CHECK (NOT is_weight_based OR mg_per_kg_max IS NOT NULL)
);

-- One rule per (subject, indication, population, route). NULLS NOT DISTINCT so the "any indication, any
-- route" row cannot be inserted twice — the default NULL-is-distinct behaviour would allow duplicates of
-- exactly the row most likely to be duplicated.
CREATE UNIQUE INDEX IF NOT EXISTS uq_dosing_rule_selector
    ON masterdata.dosing_rule (subject_kind, subject_value, indication_icd_scope, population, route)
    NULLS NOT DISTINCT;

CREATE INDEX IF NOT EXISTS ix_dosing_rule_subject
    ON masterdata.dosing_rule (subject_kind, subject_value) WHERE is_active;

COMMENT ON TABLE masterdata.dosing_rule IS
    'Indication- and population-keyed dosing. Selection is MOST SPECIFIC first: an indication scope beats '
    '"any indication", and a matching population beats none. The recommended range is displayed beside the '
    'entered dose with its citation — more useful than a pass/fail, because it informs the override.';

-- ============================================================================================================
-- SEED — a small, high-risk, pharmacist-reviewed set (design 44 §4).
-- ============================================================================================================
-- Deliberately narrow. A dose cannot be derived from label prose, so what is defensible is a curated subset;
-- outside it the check reports "no dosing rule configured" and shows the manufacturer's own dosing text as
-- reference, explicitly NOT compared with what was prescribed. That behaviour is already correct and stays.
--
-- Paracetamol leads because it is the drug this population is most often given, the one most often
-- duplicated across two products (Gate 5), and the one whose overdose is quiet until it is not.

INSERT INTO masterdata.dosing_rule (
    rule_id, subject_kind, subject_value, indication_icd_scope, population, route,
    dose_unit, min_single, max_single, typical_daily, max_daily, max_duration_days,
    is_weight_based, mg_per_kg_min, mg_per_kg_max, weight_capped_at_adult_dose,
    requires_renal_function, renal_adjustment_note,
    citation, source, source_release, reviewed_by, reviewed_at, is_active)
VALUES
-- ---- paracetamol ------------------------------------------------------------------------------------------
(gen_random_uuid(), 'Ingredient', 'paracetamol', NULL, 'Adult', 'PO',
 'mg', 500, 1000, 3000, 4000, 14,
 false, NULL, NULL, true, false, NULL,
 'BNF 87, paracetamol — adult oral dosing; MHRA guidance on maximum daily dose',
 'phase-28 pharmacist review', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

(gen_random_uuid(), 'Ingredient', 'paracetamol', NULL, 'Child', 'PO',
 'mg', NULL, NULL, NULL, 4000, 14,
 -- 15 mg/kg per dose, four doses daily, capped at the adult maximum. The cap is the point: a 60kg
 -- twelve-year-old computes to 3600mg on the mg/kg rule and must not be allowed past 4000mg because the
 -- arithmetic said so.
 true, 10, 15, true, false, NULL,
 'BNFC 2024, paracetamol — dosing by body weight',
 'phase-28 pharmacist review', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

-- ---- amoxicillin, where the INDICATION genuinely changes the number ---------------------------------------
(gen_random_uuid(), 'Ingredient', 'amoxicillin', NULL, 'Adult', 'PO',
 'mg', 250, 1000, 1500, 3000, 14,
 false, NULL, NULL, true, true,
 'Reduce the frequency in significant renal impairment; eGFR is not available on this platform.',
 'BNF 87, amoxicillin — adult oral dosing',
 'phase-28 pharmacist review', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

(gen_random_uuid(), 'Ingredient', 'amoxicillin', 'H66', 'Child', 'PO',
 -- Otitis media takes a higher mg/kg than the general paediatric rule, which is exactly the case a
 -- single per-drug ceiling cannot express.
 'mg', NULL, NULL, NULL, 3000, 7,
 true, 40, 90, true, true,
 'Reduce the frequency in significant renal impairment; eGFR is not available on this platform.',
 'AAP Clinical Practice Guideline, acute otitis media (Lieberthal et al. 2013); BNFC 2024',
 'phase-28 pharmacist review', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

(gen_random_uuid(), 'Ingredient', 'amoxicillin', NULL, 'Child', 'PO',
 'mg', NULL, NULL, NULL, 3000, 14,
 true, 20, 40, true, true,
 'Reduce the frequency in significant renal impairment; eGFR is not available on this platform.',
 'BNFC 2024, amoxicillin — dosing by body weight',
 'phase-28 pharmacist review', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

-- ---- ibuprofen --------------------------------------------------------------------------------------------
(gen_random_uuid(), 'Ingredient', 'ibuprofen', NULL, 'Adult', 'PO',
 'mg', 200, 800, 1200, 2400, 10,
 false, NULL, NULL, true, true,
 'Avoid in significant renal impairment; eGFR is not available on this platform.',
 'BNF 87, ibuprofen — adult oral dosing',
 'phase-28 pharmacist review', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

(gen_random_uuid(), 'Ingredient', 'ibuprofen', NULL, 'Child', 'PO',
 'mg', NULL, NULL, NULL, 2400, 7,
 true, 5, 10, true, true,
 'Avoid in significant renal impairment; eGFR is not available on this platform.',
 'BNFC 2024, ibuprofen — dosing by body weight',
 'phase-28 pharmacist review', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

-- ---- metformin, renally cleared and therefore explicitly not checkable today -------------------------------
(gen_random_uuid(), 'Ingredient', 'metformin', NULL, 'Adult', 'PO',
 'mg', 500, 1000, 1500, 2000, NULL,
 false, NULL, NULL, true, true,
 'Reduce below eGFR 45 and stop below 30. eGFR is not available on this platform, so the dose is reported as not checked rather than endorsed.',
 'NICE NG28, Type 2 diabetes in adults; BNF 87, metformin',
 'phase-28 pharmacist review', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true)
ON CONFLICT DO NOTHING;
