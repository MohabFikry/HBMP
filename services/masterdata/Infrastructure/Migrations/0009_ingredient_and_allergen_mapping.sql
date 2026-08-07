-- masterdata-service — 0009 the ingredient model, and the allergen mappings that make the allergy check
-- capable of matching at all (44-clinical-validation-hardening §1.1–§1.3, phase 28 Gate 1). Idempotent.
--
-- ============================================================================================================
-- WHY AN INGREDIENT TABLE, AND WHY IT ARRIVES WITH THE ALLERGY FIX RATHER THAN WITH INTERACTIONS
-- ============================================================================================================
-- The allergy matcher compared a recorded allergen CODE ('ALG-PENICILLIN') against the drug's ATC ancestor
-- chain ('J', 'J01', 'J01C'). Two disjoint code spaces, so the comparison could never be true — and the
-- prescribing engine rendered that as "no conflict with the 3 recorded allergies". A false negative
-- presented as positive assurance.
--
-- Fixing it properly means asking the right question: not "is this allergen code in the drug's ATC chain"
-- but "does this product CONTAIN a molecule this patient reacts to". That question needs products resolved
-- into molecules, which is what these two tables are. Interactions (Gate 3) and duplicate therapy (Gate 5)
-- need exactly the same resolution, so it is built once, here, rather than twice.
--
-- ============================================================================================================
-- WHY THE KEY IS A NORMALISED INN NAME AND NOT A UUID OR AN ATC-5 CODE
-- ============================================================================================================
-- Clinical governance (Gate 11) requires every rule to be reviewed by a named pharmacist before it goes
-- active. A reviewer proofreading `subject_value = '0192f3a1-…'` is not proofreading anything, so the key is
-- the molecule's name, normalised: 'warfarin', 'amoxicillin'.
--
-- ATC-5 was the other candidate and it fails on exactly the products these checks exist for. 14.8% of the
-- catalogue carries no ATC code at all, and a combination product carries ONE ATC for the compound —
-- co-amoxiclav is J01CR02, which cannot decompose into amoxicillin and clavulanic acid. Keying on ATC would
-- make the paracetamol-hidden-inside-a-cold-remedy overdose (Gate 5) undetectable by construction.
--
-- ATC is still carried, on the ingredient and as a scope on an allergen, because "all penicillins" is a real
-- clinical statement that an enumeration of molecules expresses badly and survives new products worse.

-- ------------------------------------------------------------------------------------------------------
-- The molecules.
-- ------------------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS masterdata.ingredient (
    ingredient_id   uuid PRIMARY KEY,
    -- The normalised INN name, and the key every clinical rule points at. Lower-case, whitespace collapsed,
    -- trailing salt or hydrate form removed — see Mersal.Ingredients.IngredientTokens, which is the ONE
    -- implementation of this normalisation on the platform (asserted by OneIngredientNormaliserTests).
    ingredient_key  text NOT NULL UNIQUE,
    name_en         text NOT NULL,
    name_ar         text,
    -- The substance-level ATC where the catalogue supplies one. Nullable and often null: 14.8% of products
    -- carry no ATC, and a combination product's ATC belongs to the compound rather than to this molecule.
    atc_code        text,
    -- Reserved for a future RxNorm alignment. Nothing populates it; carried so the column does not need a
    -- migration the day an external mapping arrives.
    rxcui           text,
    is_active       boolean NOT NULL DEFAULT true,
    source          varchar(64) NOT NULL,
    source_release  varchar(64),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE masterdata.ingredient IS
    'One row per active molecule. ingredient_key (normalised INN) is the business key every clinical rule '
    'is written against; ingredient_id is a surrogate derived from it and stable across reloads.';

-- ------------------------------------------------------------------------------------------------------
-- What each product is made of. A COMBINATION PRODUCT PRODUCES MULTIPLE ROWS — that is the point.
-- ------------------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS masterdata.drug_ingredient (
    drug_id         uuid NOT NULL REFERENCES masterdata.drug(drug_id) ON DELETE CASCADE,
    ingredient_key  text NOT NULL REFERENCES masterdata.ingredient(ingredient_key) ON DELETE RESTRICT,
    -- Position within the product as the source lists it. No clinical ranking is implied: the source
    -- expresses none, and inventing one would be fabrication.
    ordinal         int  NOT NULL DEFAULT 0,
    strength        text,
    source_release  varchar(64),
    PRIMARY KEY (drug_id, ingredient_key)
);

CREATE INDEX IF NOT EXISTS ix_drug_ingredient_key ON masterdata.drug_ingredient (ingredient_key);

COMMENT ON TABLE masterdata.drug_ingredient IS
    'Product → molecules. Co-amoxiclav is TWO rows. A product with no resolvable ingredient has NONE, and '
    'that absence is load-bearing: the ingredient-level checks must report it, never pass it.';

-- ------------------------------------------------------------------------------------------------------
-- Cross-reactivity, modelled on the evidence rather than on the folklore (design 44 §8).
-- ------------------------------------------------------------------------------------------------------
-- The historically quoted ~10% penicillin/cephalosporin cross-reactivity figure is not supported by modern
-- evidence. Risk tracks R1 SIDE-CHAIN similarity, not the shared beta-lactam ring, and it is low. This
-- matters in both directions: under-warning misses a real reaction, and blanket cephalosporin avoidance
-- after a penicillin label causes real harm through inferior antibiotic choice — a refugee clinic that
-- reaches for a second-line antibiotic on a folklore percentage treats worse and pays more.
--
-- So confidence is a COLUMN, it is mandatory, and the finding text states it. A prescriber told "possible
-- cross-reaction, low confidence, side chains differ" can weigh that. One told "allergy conflict" cannot.
CREATE TABLE IF NOT EXISTS masterdata.cross_reactivity_group (
    group_code    varchar(32) PRIMARY KEY,
    name_en       text NOT NULL,
    name_ar       text NOT NULL,
    confidence    text NOT NULL CHECK (confidence IN ('High','Moderate','Low','Theoretical')),
    -- Shown to the prescriber with the finding. This is where the nuance lives — "shares the aminobenzyl
    -- side chain" is the actionable sentence, not the word "cross-reactivity".
    statement_en  text NOT NULL,
    statement_ar  text NOT NULL,
    citation      text NOT NULL,
    source        text NOT NULL,
    reviewed_by   text NOT NULL,
    reviewed_at   timestamptz NOT NULL DEFAULT now()
);

-- A group's members are molecules, or a whole ATC class where enumerating molecules would be worse. Exactly
-- one of the two per row.
CREATE TABLE IF NOT EXISTS masterdata.cross_reactivity_member (
    group_code      varchar(32) NOT NULL REFERENCES masterdata.cross_reactivity_group(group_code) ON DELETE CASCADE,
    ingredient_key  text REFERENCES masterdata.ingredient(ingredient_key) ON DELETE RESTRICT,
    atc_scope       text,
    CONSTRAINT ck_cross_reactivity_member_one_target
        CHECK (num_nonnulls(ingredient_key, atc_scope) = 1)
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_cross_reactivity_member_ingredient
    ON masterdata.cross_reactivity_member (group_code, ingredient_key) WHERE ingredient_key IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS uq_cross_reactivity_member_atc
    ON masterdata.cross_reactivity_member (group_code, atc_scope) WHERE atc_scope IS NOT NULL;

-- ------------------------------------------------------------------------------------------------------
-- The allergen mapping itself.
-- ------------------------------------------------------------------------------------------------------
ALTER TABLE masterdata.allergen
    -- "All penicillins" as one durable statement. An enumeration of molecules would need editing every time
    -- a product enters the market; an ATC scope does not.
    ADD COLUMN IF NOT EXISTS atc_scopes           text[] NOT NULL DEFAULT '{}',
    -- FALSE for food and environmental allergens. This is NOT the same as "unmapped": a peanut allergy is
    -- not a question about a medicine, whereas an unmapped drug allergen is a gap in our catalogue. The
    -- engine reports the two differently, and conflating them would make every patient with a dust-mite
    -- allergy look like a coverage failure.
    ADD COLUMN IF NOT EXISTS is_drug_mappable     boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS mapping_source       text,
    ADD COLUMN IF NOT EXISTS mapping_reviewed_by  text,
    ADD COLUMN IF NOT EXISTS mapping_reviewed_at  timestamptz;

-- Clinical governance, as a constraint rather than a convention (Gate 11): a mapping that decides whether a
-- prescriber is warned must have a named reviewer. An unreviewed mapping is not better than no mapping — it
-- produces confident findings from unattributable judgement, which is precisely what doc 43 §1 rule 2 bans.
-- Added only if absent, rather than dropped and re-added. A DROP would make this migration idempotent by
-- leaving a window — however short — in which the constraint does not exist, and a rolling deployment can
-- write through that window. An expand-only ADD has no such window.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_allergen_mapping_reviewed'
          AND conrelid = 'masterdata.allergen'::regclass)
    THEN
        ALTER TABLE masterdata.allergen ADD CONSTRAINT ck_allergen_mapping_reviewed
            CHECK (atc_scopes = '{}' OR (mapping_reviewed_by IS NOT NULL AND mapping_source IS NOT NULL));
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS masterdata.allergen_ingredient (
    allergen_id     uuid NOT NULL REFERENCES masterdata.allergen(allergen_id) ON DELETE CASCADE,
    ingredient_key  text NOT NULL REFERENCES masterdata.ingredient(ingredient_key) ON DELETE RESTRICT,
    -- NOT NULL by construction rather than by CHECK: every row here is a pharmacist's decision that a
    -- molecule is what a recorded allergy means, and a row without one should be impossible to insert.
    source          text NOT NULL,
    reviewed_by     text NOT NULL,
    reviewed_at     timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (allergen_id, ingredient_key)
);

-- An allergen may carry MORE THAN ONE cross-reactivity group, at different confidences. A penicillin label
-- implies a moderate-confidence relationship with the four cephalosporins sharing its R1 side chain and a
-- low-confidence one with cephalosporins generally, and collapsing those into a single group would force the
-- alert to state the wrong confidence for one of them.
CREATE TABLE IF NOT EXISTS masterdata.allergen_cross_reactivity (
    allergen_id  uuid        NOT NULL REFERENCES masterdata.allergen(allergen_id) ON DELETE CASCADE,
    group_code   varchar(32) NOT NULL REFERENCES masterdata.cross_reactivity_group(group_code) ON DELETE CASCADE,
    PRIMARY KEY (allergen_id, group_code)
);

-- ============================================================================================================
-- SEED — the fifteen shipped allergens, every drug one mapped and reviewed.
-- ============================================================================================================

-- Molecules the curated mappings reference by name. Seeded explicitly rather than relied upon from the
-- catalogue load: a cross-reactivity rule must not silently lose a member because no Egyptian product
-- happens to contain that molecule this month.
INSERT INTO masterdata.ingredient (ingredient_id, ingredient_key, name_en, name_ar, atc_code, source, source_release)
VALUES
    (gen_random_uuid(), 'amoxicillin',          'Amoxicillin',          'أموكسيسيلين',   'J01CA04', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'ampicillin',           'Ampicillin',           'أمبيسيلين',     'J01CA01', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'benzylpenicillin',     'Benzylpenicillin',     'بنزيل بنسلين',  'J01CE01', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'flucloxacillin',       'Flucloxacillin',       'فلوكلوكساسيلين','J01CF05', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'cefalexin',            'Cefalexin',            'سيفالكسين',     'J01DB01', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'cefaclor',             'Cefaclor',             'سيفاكلور',      'J01DC04', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'cefadroxil',           'Cefadroxil',           'سيفادروكسيل',   'J01DB05', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'cefprozil',            'Cefprozil',            'سيفبروزيل',     'J01DC10', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'sulfamethoxazole',     'Sulfamethoxazole',     'سلفاميثوكسازول','J01EC01', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'sulfadiazine',         'Sulfadiazine',         'سلفاديازين',    'J01EC02', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'sulfasalazine',        'Sulfasalazine',        'سلفاسالازين',   'A07EC01', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'acetylsalicylic acid', 'Acetylsalicylic acid', 'حمض أسيتيل ساليسيليك', 'N02BA01', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'ibuprofen',            'Ibuprofen',            'إيبوبروفين',    'M01AE01', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'diclofenac',           'Diclofenac',           'ديكلوفيناك',    'M01AB05', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'naproxen',             'Naproxen',             'نابروكسين',     'M01AE02', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'codeine',              'Codeine',              'كودايين',       'R05DA04', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'morphine',             'Morphine',             'مورفين',        'N02AA01', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'iohexol',              'Iohexol',              'إيوهيكسول',     'V08AB02', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'iopromide',            'Iopromide',            'إيوبروميد',     'V08AB05', 'phase-28 curated', 'seed-v1')
ON CONFLICT (ingredient_key) DO NOTHING;

INSERT INTO masterdata.cross_reactivity_group
    (group_code, name_en, name_ar, confidence, statement_en, statement_ar, citation, source, reviewed_by)
VALUES
    ('XR-PEN-CEPH-R1',
     'Aminopenicillin → cephalosporin, shared R1 side chain',
     'أمينوبنسلين ← سيفالوسبورين، سلسلة جانبية R1 مشتركة',
     'Moderate',
     'This cephalosporin shares the aminobenzyl R1 side chain with the recorded penicillin allergy, which is '
     'where the cross-reactivity risk actually lies — not in the shared beta-lactam ring. Consider a '
     'cephalosporin with a different side chain, or specialist advice if the reaction was severe.',
     'يشترك هذا السيفالوسبورين مع البنسلين المسجَّل في السلسلة الجانبية R1، وهنا يكمن خطر التفاعل المتبادل '
     'فعليًا وليس في حلقة البيتالاكتام المشتركة. يُنظر في سيفالوسبورين بسلسلة جانبية مختلفة، أو استشارة '
     'أخصائي إذا كان التفاعل شديدًا.',
     'Zagursky RJ, Pichichero ME. Cross-reactivity in beta-lactam allergy. J Allergy Clin Immunol Pract. 2018;6(1):72-81',
     'phase-28 pharmacist review', 'Mersal clinical pharmacy (phase 28)'),

    ('XR-PEN-CEPH-GENERAL',
     'Penicillin → cephalosporin, no shared side chain',
     'بنسلين ← سيفالوسبورين، دون سلسلة جانبية مشتركة',
     'Low',
     'Cross-reactivity between penicillins and cephalosporins that do NOT share a side chain is low — the '
     'often-quoted 10% figure is not supported by current evidence. Avoiding all cephalosporins after a '
     'penicillin label causes measurable harm through inferior antibiotic choice. Weigh the recorded '
     'reaction: a rash is not anaphylaxis.',
     'التفاعل المتبادل بين البنسلينات والسيفالوسبورينات التي لا تشترك في سلسلة جانبية منخفض — ونسبة ١٠٪ '
     'الشائعة لا تدعمها الأدلة الحالية. تجنّب جميع السيفالوسبورينات بعد تسجيل حساسية البنسلين يسبب ضررًا '
     'ملموسًا عبر اختيار مضاد حيوي أقل ملاءمة. يُوزن نوع التفاعل المسجَّل: الطفح ليس تأقًا.',
     'Zagursky RJ, Pichichero ME. J Allergy Clin Immunol Pract. 2018;6(1):72-81; Picard M et al. J Allergy Clin Immunol Pract. 2019;7(8):2722-2738',
     'phase-28 pharmacist review', 'Mersal clinical pharmacy (phase 28)'),

    ('XR-CEPH-PEN-R1',
     'Cephalosporin → aminopenicillin, shared R1 side chain',
     'سيفالوسبورين ← أمينوبنسلين، سلسلة جانبية R1 مشتركة',
     'Moderate',
     'This penicillin shares the aminobenzyl R1 side chain with the recorded cephalosporin allergy. Consider '
     'an agent from another class, or specialist advice if the reaction was severe.',
     'يشترك هذا البنسلين مع السيفالوسبورين المسجَّل في السلسلة الجانبية R1. يُنظر في دواء من فئة أخرى، أو '
     'استشارة أخصائي إذا كان التفاعل شديدًا.',
     'Zagursky RJ, Pichichero ME. J Allergy Clin Immunol Pract. 2018;6(1):72-81',
     'phase-28 pharmacist review', 'Mersal clinical pharmacy (phase 28)'),

    ('XR-NSAID-COX',
     'NSAID → NSAID, shared COX-1 inhibition',
     'مضاد التهاب غير ستيرويدي ← آخر، تثبيط COX-1 مشترك',
     'High',
     'Reactions to one COX-1 inhibiting NSAID commonly extend to the others, because the mechanism is '
     'pharmacological rather than IgE-mediated. This includes aspirin. Paracetamol is usually tolerated; a '
     'COX-2 selective agent may be an option on specialist advice.',
     'التفاعلات تجاه أحد مضادات الالتهاب غير الستيرويدية المثبِّطة لـ COX-1 تمتد عادةً إلى بقيتها، لأن '
     'الآلية دوائية وليست بوساطة IgE، ويشمل ذلك الأسبرين. الباراسيتامول يُحتمل عادةً، وقد يكون مثبّط '
     'COX-2 الانتقائي خيارًا باستشارة أخصائي.',
     'Kowalski ML et al. Classification and practical approach to hypersensitivity to NSAIDs. Allergy. 2013;68(10):1219-1232',
     'phase-28 pharmacist review', 'Mersal clinical pharmacy (phase 28)')
ON CONFLICT (group_code) DO NOTHING;

-- Members. NOTE WHAT IS ABSENT: there is no sulfonamide-antibiotic → non-antibiotic-sulfonamide group.
-- That association (to furosemide, thiazides, sulfonylureas, celecoxib) is not supported by the evidence,
-- and over-flagging it is a documented CDS defect that withholds ordinary medicines from patients who could
-- take them safely. Leaving it out is a clinical decision, recorded here so it is not "fixed" later.
INSERT INTO masterdata.cross_reactivity_member (group_code, ingredient_key, atc_scope) VALUES
    ('XR-PEN-CEPH-R1',      'cefalexin',            NULL),
    ('XR-PEN-CEPH-R1',      'cefaclor',             NULL),
    ('XR-PEN-CEPH-R1',      'cefadroxil',           NULL),
    ('XR-PEN-CEPH-R1',      'cefprozil',            NULL),
    ('XR-PEN-CEPH-GENERAL',  NULL,                  'J01D'),
    ('XR-CEPH-PEN-R1',      'amoxicillin',          NULL),
    ('XR-CEPH-PEN-R1',      'ampicillin',           NULL),
    ('XR-NSAID-COX',         NULL,                  'M01A'),
    ('XR-NSAID-COX',        'acetylsalicylic acid', NULL)
ON CONFLICT DO NOTHING;

-- ---- The fifteen allergens -----------------------------------------------------------------------------
--
-- ATC scopes, with what each one is:
--   J01C  penicillins            J01D  other beta-lactams (cephalosporins)   J01E  sulfonamides + trimethoprim
--   M01A  non-steroidal anti-inflammatories                                  N02BA salicylic acid derivatives
--   N02A  opioids                R05DA04 codeine as an antitussive           V08A  iodinated contrast media

UPDATE masterdata.allergen SET
    atc_scopes = ARRAY['J01C'], is_drug_mappable = true,
    mapping_source = 'WHO ATC 2024 + phase-28 pharmacist review',
    mapping_reviewed_by = 'Mersal clinical pharmacy (phase 28)', mapping_reviewed_at = now()
WHERE code = 'ALG-PENICILLIN';

UPDATE masterdata.allergen SET
    atc_scopes = ARRAY['J01D'], is_drug_mappable = true,
    mapping_source = 'WHO ATC 2024 + phase-28 pharmacist review',
    mapping_reviewed_by = 'Mersal clinical pharmacy (phase 28)', mapping_reviewed_at = now()
WHERE code = 'ALG-CEPHALO';

-- J01E only. Deliberately NOT the non-antibiotic sulfonamides — see the note on cross_reactivity_member.
UPDATE masterdata.allergen SET
    atc_scopes = ARRAY['J01E'], is_drug_mappable = true,
    mapping_source = 'WHO ATC 2024 + phase-28 pharmacist review',
    mapping_reviewed_by = 'Mersal clinical pharmacy (phase 28)', mapping_reviewed_at = now()
WHERE code = 'ALG-SULFA';

UPDATE masterdata.allergen SET
    atc_scopes = ARRAY['M01A'], is_drug_mappable = true,
    mapping_source = 'WHO ATC 2024 + phase-28 pharmacist review',
    mapping_reviewed_by = 'Mersal clinical pharmacy (phase 28)', mapping_reviewed_at = now()
WHERE code = 'ALG-NSAID';

-- Aspirin sits in two ATC places: N02BA as an analgesic and B01AC06 as an antiplatelet. A patient who
-- reacts to it reacts to both, and scoping only the analgesic branch would miss every cardiac low-dose
-- product — which is the presentation most of this population is actually on.
UPDATE masterdata.allergen SET
    atc_scopes = ARRAY['N02BA', 'B01AC06'], is_drug_mappable = true,
    mapping_source = 'WHO ATC 2024 + phase-28 pharmacist review',
    mapping_reviewed_by = 'Mersal clinical pharmacy (phase 28)', mapping_reviewed_at = now()
WHERE code = 'ALG-ASPIRIN';

UPDATE masterdata.allergen SET
    atc_scopes = ARRAY['N02A', 'R05DA04'], is_drug_mappable = true,
    mapping_source = 'WHO ATC 2024 + phase-28 pharmacist review',
    mapping_reviewed_by = 'Mersal clinical pharmacy (phase 28)', mapping_reviewed_at = now()
WHERE code = 'ALG-CODEINE';

UPDATE masterdata.allergen SET
    atc_scopes = ARRAY['V08A'], is_drug_mappable = true,
    mapping_source = 'WHO ATC 2024 + phase-28 pharmacist review',
    mapping_reviewed_by = 'Mersal clinical pharmacy (phase 28)', mapping_reviewed_at = now()
WHERE code = 'ALG-IODINE';

-- Food and environmental allergens: mappable = FALSE. Not a gap in the catalogue — simply not a question
-- about a medicine. The engine says so in those words rather than reporting them as unchecked.
UPDATE masterdata.allergen SET
    is_drug_mappable = false,
    mapping_source = 'phase-28 pharmacist review — not a medicine-related allergen',
    mapping_reviewed_by = 'Mersal clinical pharmacy (phase 28)', mapping_reviewed_at = now()
WHERE category IN ('Food', 'Environmental');

-- Exact-molecule mappings, alongside the ATC scopes above. The scope answers "any penicillin"; these answer
-- the molecules a scope would miss — sulfasalazine is A07EC01, an intestinal anti-inflammatory, and no
-- sulfonamide ATC scope reaches it.
INSERT INTO masterdata.allergen_ingredient (allergen_id, ingredient_key, source, reviewed_by)
SELECT a.allergen_id, m.key, 'phase-28 pharmacist review', 'Mersal clinical pharmacy (phase 28)'
FROM masterdata.allergen a
JOIN (VALUES
    ('ALG-PENICILLIN', 'amoxicillin'),
    ('ALG-PENICILLIN', 'ampicillin'),
    ('ALG-PENICILLIN', 'benzylpenicillin'),
    ('ALG-PENICILLIN', 'flucloxacillin'),
    ('ALG-CEPHALO',    'cefalexin'),
    ('ALG-CEPHALO',    'cefaclor'),
    ('ALG-CEPHALO',    'cefadroxil'),
    ('ALG-CEPHALO',    'cefprozil'),
    ('ALG-SULFA',      'sulfamethoxazole'),
    ('ALG-SULFA',      'sulfadiazine'),
    ('ALG-SULFA',      'sulfasalazine'),
    ('ALG-NSAID',      'ibuprofen'),
    ('ALG-NSAID',      'diclofenac'),
    ('ALG-NSAID',      'naproxen'),
    ('ALG-ASPIRIN',    'acetylsalicylic acid'),
    ('ALG-CODEINE',    'codeine'),
    ('ALG-CODEINE',    'morphine'),
    ('ALG-IODINE',     'iohexol'),
    ('ALG-IODINE',     'iopromide')
) AS m(code, key) ON m.code = a.code
ON CONFLICT DO NOTHING;

INSERT INTO masterdata.allergen_cross_reactivity (allergen_id, group_code)
SELECT a.allergen_id, m.grp
FROM masterdata.allergen a
JOIN (VALUES
    ('ALG-PENICILLIN', 'XR-PEN-CEPH-R1'),
    ('ALG-PENICILLIN', 'XR-PEN-CEPH-GENERAL'),
    ('ALG-CEPHALO',    'XR-CEPH-PEN-R1'),
    ('ALG-NSAID',      'XR-NSAID-COX'),
    ('ALG-ASPIRIN',    'XR-NSAID-COX')
) AS m(code, grp) ON m.code = a.code
ON CONFLICT DO NOTHING;
