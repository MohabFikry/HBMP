-- masterdata-service — 0014 the penicillin molecules the catalogue actually contains (phase 28 Gate 1,
-- follow-up). Idempotent.
--
-- ============================================================================================================
-- FOUND BY LOADING THE REAL CATALOGUE, NOT BY READING THE DESIGN
-- ============================================================================================================
-- Migration 0009 mapped ALG-PENICILLIN to amoxicillin, ampicillin, benzylpenicillin and flucloxacillin —
-- the names a pharmacist writes. Deriving masterdata.drug_ingredient from the 22,653 real products showed
-- what the Egyptian catalogue actually writes:
--
--   benzathine penicillin g       9 products      phenoxymethyl penicillin    3 products
--   penicillin g                  4 products      benzylpenicillin            0 products
--
-- These are not spelling variants — they are different salts and esters of penicillin, and a patient who
-- reacts to one reacts to the class. The ATC scope J01C already catches them, so the check was never blind;
-- but the EXACT-ingredient path missed, which is the path that produces the specific, actionable message
-- ("contains benzathine penicillin g") rather than the general one ("belongs to ATC class J01C").
--
-- The orthography fold added alongside this migration handles the other half of the gap: amoxycillin,
-- sulphamethoxazole, sulphasalazine and sulphadiazine now resolve to their INN spellings, so 0009's
-- mappings reach them without needing to be restated here.

INSERT INTO masterdata.ingredient (ingredient_id, ingredient_key, name_en, name_ar, atc_code, source, source_release)
VALUES
    (gen_random_uuid(), 'penicillin g',             'Penicillin G',             'بنسلين جي',        'J01CE01', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'benzathine penicillin g',  'Benzathine penicillin G',  'بنزاثين بنسلين جي','J01CE08', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'phenoxymethyl penicillin', 'Phenoxymethylpenicillin',  'فينوكسي ميثيل بنسلين','J01CE02','phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'procaine penicillin',      'Procaine penicillin',      'بروكايين بنسلين',  'J01CE09', 'phase-28 curated', 'seed-v1')
ON CONFLICT (ingredient_key) DO NOTHING;

INSERT INTO masterdata.allergen_ingredient (allergen_id, ingredient_key, source, reviewed_by)
SELECT a.allergen_id, m.key, 'phase-28 pharmacist review (catalogue reconciliation)',
       'Mersal clinical pharmacy (phase 28)'
FROM masterdata.allergen a
JOIN (VALUES
    ('ALG-PENICILLIN', 'penicillin g'),
    ('ALG-PENICILLIN', 'benzathine penicillin g'),
    ('ALG-PENICILLIN', 'phenoxymethyl penicillin'),
    ('ALG-PENICILLIN', 'procaine penicillin')
) AS m(code, key) ON m.code = a.code
ON CONFLICT DO NOTHING;

-- Silver sulfadiazine is a TOPICAL sulfonamide and the catalogue carries 26 products of it. A recorded
-- sulfonamide allergy is a genuine contraindication to it — burn dressings are exactly where it is reached
-- for, and exactly where nobody stops to re-read the allergy list.
INSERT INTO masterdata.ingredient (ingredient_id, ingredient_key, name_en, name_ar, atc_code, source, source_release)
VALUES (gen_random_uuid(), 'silver sulfadiazine', 'Silver sulfadiazine', 'سلفاديازين الفضة', 'D06BA01', 'phase-28 curated', 'seed-v1')
ON CONFLICT (ingredient_key) DO NOTHING;

INSERT INTO masterdata.allergen_ingredient (allergen_id, ingredient_key, source, reviewed_by)
SELECT a.allergen_id, 'silver sulfadiazine', 'phase-28 pharmacist review (catalogue reconciliation)',
       'Mersal clinical pharmacy (phase 28)'
FROM masterdata.allergen a WHERE a.code = 'ALG-SULFA'
ON CONFLICT DO NOTHING;
