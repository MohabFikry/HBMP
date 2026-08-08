-- masterdata-service — 0010 ingredient-level drug interactions (44-clinical-validation-hardening §1.2, §8,
-- phase 28 Gate 3). Idempotent.
--
-- ============================================================================================================
-- WHY THE OLD TABLE IS RETIRED RATHER THAN POPULATED
-- ============================================================================================================
-- masterdata.drug_interaction(drug_a_id uuid, drug_b_id uuid) keys a pair on two PRODUCTS. The Egyptian
-- catalogue holds 22,653 of them. Interactions are a property of active ingredients, not of trade names, so
-- product-level encoding means every ingredient pair must be replicated across the cartesian product of
-- every brand containing each ingredient — warfarin × NSAIDs alone would be hundreds of rows, and would need
-- extending every time a product entered the market.
--
-- That is why the table has zero rows and would have stayed empty. It was never a data-entry backlog; it was
-- an unpopulatable model. It is left in place (empty, and now documented as superseded) rather than dropped,
-- because dropping a table is not reversible in a deployment and nothing reads it once the port is switched.
--
-- ONE curated pair — warfarin × NSAIDs, keyed on the molecule and the ATC class — now covers every brand of
-- each, in both directions, and keeps covering them as the market changes.

COMMENT ON TABLE masterdata.drug_interaction IS
    'SUPERSEDED by masterdata.interaction_rule (phase 28 Gate 3). Product-level pairs are unpopulatable: '
    'interactions are a property of ingredients, and this table would need one row per pair of BRANDS. '
    'Retained empty; no code reads it.';

CREATE TABLE IF NOT EXISTS masterdata.interaction_rule (
    rule_id          uuid PRIMARY KEY,

    -- The pair, each side either a molecule or a whole ATC class. Class-level entries are what make the list
    -- maintainable: 'M01A' says "all NSAIDs" in one row and keeps saying it as new products arrive, which
    -- an enumeration of ingredients never does.
    subject_kind     text NOT NULL CHECK (subject_kind IN ('Ingredient','AtcClass')),
    subject_value    text NOT NULL,
    object_kind      text NOT NULL CHECK (object_kind IN ('Ingredient','AtcClass')),
    object_value     text NOT NULL,

    severity         text NOT NULL CHECK (severity IN ('Minor','Moderate','Major','Contraindicated')),

    -- The three fields that make an alert actionable (design 44 §3). "Major interaction with clarithromycin"
    -- tells a prescriber nothing they can do; "CYP3A4 inhibition → 10-fold simvastatin exposure →
    -- rhabdomyolysis; suspend the statin for the antibiotic course" tells them exactly what to do.
    -- MANAGEMENT is the field most likely to change the prescription, so it is NOT NULL.
    mechanism_en     text NOT NULL,
    mechanism_ar     text NOT NULL,
    clinical_effect_en text NOT NULL,
    clinical_effect_ar text NOT NULL,
    management_en    text NOT NULL,
    management_ar    text NOT NULL,

    onset            text NOT NULL DEFAULT 'Unknown' CHECK (onset IN ('Rapid','Delayed','Unknown')),
    evidence_level   text NOT NULL CHECK (evidence_level IN ('Established','Probable','Theoretical')),

    -- Clinical governance, as a constraint rather than a convention (design 44 §11). No active rule without
    -- a citation and a named pharmacist. An unattributable advisory is one a clinician is right to ignore.
    citation         text NOT NULL,
    source           text NOT NULL,
    source_release   varchar(64),
    reviewed_by      text,
    reviewed_at      timestamptz,
    is_active        boolean NOT NULL DEFAULT false,

    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),

    -- A rule may not interact with itself, which would fire on every prescription containing the molecule.
    CONSTRAINT ck_interaction_rule_distinct
        CHECK (NOT (subject_kind = object_kind AND subject_value = object_value)),

    -- Same pattern as phase 27's activation constraint: a row may only go live with a reviewer on it.
    CONSTRAINT ck_interaction_rule_reviewed
        CHECK (NOT is_active OR (reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL))
);

-- The pair is UNORDERED. warfarin × NSAIDs and NSAIDs × warfarin are one rule, and the index enforces it by
-- normalising the two sides into a canonical order rather than trusting the author to pick one — a duplicate
-- entered the other way round would fire twice on the same prescription and read as two separate risks.
CREATE UNIQUE INDEX IF NOT EXISTS uq_interaction_rule_pair ON masterdata.interaction_rule (
    LEAST(subject_kind || ':' || subject_value, object_kind || ':' || object_value),
    GREATEST(subject_kind || ':' || subject_value, object_kind || ':' || object_value)
);

CREATE INDEX IF NOT EXISTS ix_interaction_rule_subject
    ON masterdata.interaction_rule (subject_kind, subject_value) WHERE is_active;
CREATE INDEX IF NOT EXISTS ix_interaction_rule_object
    ON masterdata.interaction_rule (object_kind, object_value) WHERE is_active;

-- ============================================================================================================
-- SEED — the ONC/NLM high-priority list, prioritised by what Mersal dispenses (design 44 §8).
-- ============================================================================================================
-- A charity cannot licence a comprehensive interaction database, and pretending otherwise would be worse
-- than curating. The published high-priority DDI work (Phansalkar et al., ONC/NLM) exists precisely to
-- define a short, citable set intended for INTERRUPTIVE alerting — a few dozen ingredient-level pairs cover
-- the majority of preventable harm.
--
-- Coverage is partial by construction, and the UI states the pair count and the date. A partial list is
-- ethical to ship only when its partiality is visible.

-- Molecules the rules below name. Seeded here for the same reason as 0009's: a rule must not lose a side
-- because no Egyptian product happens to contain that molecule this month.
INSERT INTO masterdata.ingredient (ingredient_id, ingredient_key, name_en, name_ar, atc_code, source, source_release)
VALUES
    (gen_random_uuid(), 'warfarin',        'Warfarin',        'وارفارين',      'B01AA03', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'clarithromycin',  'Clarithromycin',  'كلاريثروميسين', 'J01FA09', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'simvastatin',     'Simvastatin',     'سيمفاستاتين',   'C10AA01', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'methotrexate',    'Methotrexate',    'ميثوتريكسات',   'L01BA01', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'trimethoprim',    'Trimethoprim',    'تريميثوبريم',   'J01EA01', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'spironolactone',  'Spironolactone',  'سبيرونولاكتون', 'C03DA01', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'fluconazole',     'Fluconazole',     'فلوكونازول',    'J02AC01', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'ciprofloxacin',   'Ciprofloxacin',   'سيبروفلوكساسين','J01MA02', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'amiodarone',      'Amiodarone',      'أميودارون',     'C01BD01', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'metformin',       'Metformin',       'ميتفورمين',     'A10BA02', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'tramadol',        'Tramadol',        'ترامادول',      'N02AX02', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'linezolid',       'Linezolid',       'لينزوليد',      'J01XX08', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'potassium chloride', 'Potassium chloride', 'كلوريد البوتاسيوم', 'A12BA01', 'phase-28 curated', 'seed-v1')
ON CONFLICT (ingredient_key) DO NOTHING;

INSERT INTO masterdata.interaction_rule (
    rule_id, subject_kind, subject_value, object_kind, object_value, severity,
    mechanism_en, mechanism_ar, clinical_effect_en, clinical_effect_ar, management_en, management_ar,
    onset, evidence_level, citation, source, source_release, reviewed_by, reviewed_at, is_active)
VALUES
-- ---- warfarin, the single highest-yield molecule on any interruptive list -------------------------------
(gen_random_uuid(), 'Ingredient', 'warfarin', 'AtcClass', 'M01A', 'Major',
 'NSAIDs inhibit platelet aggregation and damage gastric mucosa; some also displace warfarin from protein binding.',
 'مضادات الالتهاب غير الستيرويدية تثبّط تجمّع الصفائح وتضرّ بالغشاء المخاطي للمعدة، وبعضها يزيح الوارفارين عن الارتباط البروتيني.',
 'Markedly increased risk of gastrointestinal and other major bleeding, often without a rise in INR.',
 'ارتفاع ملحوظ في خطر النزيف الهضمي وغيره من النزيف الشديد، وغالبًا دون ارتفاع في INR.',
 'Avoid the combination. Use paracetamol for analgesia. If an NSAID is unavoidable, add gastroprotection and monitor INR and haemoglobin closely.',
 'يُتجنّب هذا الجمع. يُستخدم الباراسيتامول للألم. وإذا تعذّر تجنّب مضاد الالتهاب، تُضاف وقاية معدية مع متابعة دقيقة لـ INR والهيموجلوبين.',
 'Delayed', 'Established',
 'Phansalkar S et al. High-priority drug-drug interactions for use in electronic health records. JAMIA 2012;19(5):735-743',
 'ONC/NLM high-priority DDI list', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

(gen_random_uuid(), 'Ingredient', 'warfarin', 'Ingredient', 'fluconazole', 'Major',
 'Fluconazole inhibits CYP2C9, the main route of S-warfarin clearance.',
 'الفلوكونازول يثبّط إنزيم CYP2C9 المسؤول الرئيسي عن استقلاب الوارفارين.',
 'INR can rise several-fold within days, with a corresponding bleeding risk.',
 'قد يرتفع INR عدة أضعاف خلال أيام مع ارتفاع مقابل في خطر النزيف.',
 'Use a topical azole where the indication allows. If systemic treatment is required, reduce the warfarin dose and check INR within 3-5 days.',
 'يُفضَّل مضاد فطريات موضعي إن أمكن. وإذا لزم العلاج الجهازي، تُخفَّض جرعة الوارفارين ويُفحص INR خلال ٣-٥ أيام.',
 'Delayed', 'Established',
 'Phansalkar S et al. JAMIA 2012;19(5):735-743',
 'ONC/NLM high-priority DDI list', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

(gen_random_uuid(), 'Ingredient', 'warfarin', 'Ingredient', 'amiodarone', 'Major',
 'Amiodarone inhibits CYP2C9 and CYP3A4, and has a half-life of weeks.',
 'الأميودارون يثبّط CYP2C9 و CYP3A4، وعمر النصف له أسابيع.',
 'INR rises progressively over 1-3 weeks and stays elevated long after the amiodarone is stopped.',
 'يرتفع INR تدريجيًا خلال ١-٣ أسابيع ويظل مرتفعًا لفترة طويلة بعد إيقاف الأميودارون.',
 'Reduce the warfarin dose by roughly a third when amiodarone is started and monitor INR weekly for a month. Do not reverse the reduction when amiodarone stops without re-checking.',
 'تُخفَّض جرعة الوارفارين نحو الثلث عند بدء الأميودارون مع فحص INR أسبوعيًا لمدة شهر، ولا يُعاد رفعها عند إيقافه دون إعادة الفحص.',
 'Delayed', 'Established',
 'Phansalkar S et al. JAMIA 2012;19(5):735-743',
 'ONC/NLM high-priority DDI list', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

-- ---- the "triple whammy": NSAID + ACEI/ARB + diuretic ----------------------------------------------------
-- Encoded as two pairwise rules because the engine matches pairs. Both fire on a prescription carrying all
-- three, which is the presentation that causes acute kidney injury in primary care.
(gen_random_uuid(), 'AtcClass', 'M01A', 'AtcClass', 'C09', 'Major',
 'NSAIDs remove the prostaglandin-mediated afferent arteriolar dilation that maintains glomerular filtration when efferent tone is already reduced by ACE inhibition or receptor blockade.',
 'مضادات الالتهاب غير الستيرويدية تلغي التوسّع الشرياني الوارد المعتمد على البروستاجلاندين، والذي يحافظ على الترشيح الكبيبي عند انخفاض المقاومة الصادرة بفعل مثبطات الإنزيم المحوّل أو حاصرات المستقبل.',
 'Acute kidney injury and hyperkalaemia, especially in dehydration, older age or existing renal impairment. Risk rises sharply if a diuretic is also present (the "triple whammy").',
 'إصابة كلوية حادة وفرط بوتاسيوم الدم، خاصة مع الجفاف أو التقدّم في السن أو القصور الكلوي. ويرتفع الخطر بشدة عند إضافة مدرّ للبول.',
 'Avoid the NSAID; use paracetamol. If unavoidable, keep the course short, ensure hydration, and check creatinine and potassium within a week.',
 'يُتجنّب مضاد الالتهاب ويُستخدم الباراسيتامول. وإن تعذّر، تُقصَّر المدة مع ضمان الإماهة وفحص الكرياتينين والبوتاسيوم خلال أسبوع.',
 'Delayed', 'Established',
 'Lapi F et al. Concurrent use of diuretics, ACEIs or ARBs with NSAIDs and risk of acute kidney injury. BMJ 2013;346:e8525',
 'ONC/NLM high-priority DDI list', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

(gen_random_uuid(), 'AtcClass', 'M01A', 'AtcClass', 'C03', 'Moderate',
 'NSAIDs cause sodium and water retention, opposing the diuretic, and reduce renal perfusion.',
 'مضادات الالتهاب غير الستيرويدية تسبّب احتباس الصوديوم والماء وتضعف أثر المدرّ وتقلّل التروية الكلوية.',
 'Loss of blood-pressure and oedema control, and — with an ACE inhibitor or ARB also present — acute kidney injury.',
 'فقدان السيطرة على ضغط الدم والوذمة، ومع وجود مثبط الإنزيم المحوّل أو حاصر المستقبل تحدث إصابة كلوية حادة.',
 'Prefer paracetamol. Review renal function and blood pressure if the NSAID continues beyond a few days.',
 'يُفضَّل الباراسيتامول، وتُراجَع وظائف الكلى وضغط الدم إذا استمر مضاد الالتهاب أكثر من بضعة أيام.',
 'Delayed', 'Probable',
 'Lapi F et al. BMJ 2013;346:e8525',
 'ONC/NLM high-priority DDI list', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

-- ---- CYP3A4 inhibitors with simvastatin ------------------------------------------------------------------
(gen_random_uuid(), 'Ingredient', 'simvastatin', 'Ingredient', 'clarithromycin', 'Contraindicated',
 'Clarithromycin is a potent CYP3A4 inhibitor and simvastatin is cleared almost entirely by that route.',
 'الكلاريثروميسين مثبّط قوي لـ CYP3A4، والسيمفاستاتين يُستقلب بهذا المسار كليًا تقريبًا.',
 'Up to a 10-fold rise in simvastatin exposure, causing myopathy, rhabdomyolysis and acute kidney injury.',
 'ارتفاع تعرّض السيمفاستاتين حتى عشرة أضعاف، مسبّبًا اعتلال العضلات وانحلال الربيدات وإصابة كلوية حادة.',
 'Suspend the simvastatin for the duration of the antibiotic course, or use azithromycin instead. Tell the patient to report muscle pain or dark urine.',
 'يُوقف السيمفاستاتين طوال مدة المضاد الحيوي، أو يُستبدل بالأزيثروميسين. ويُنبَّه المريض للإبلاغ عن ألم عضلي أو بول داكن.',
 'Rapid', 'Established',
 'Phansalkar S et al. JAMIA 2012;19(5):735-743; FDA Drug Safety Communication, simvastatin dose limitations, 2011',
 'ONC/NLM high-priority DDI list', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

-- ---- methotrexate, where the harm is cumulative and quiet -------------------------------------------------
(gen_random_uuid(), 'Ingredient', 'methotrexate', 'AtcClass', 'M01A', 'Major',
 'NSAIDs reduce renal tubular secretion of methotrexate and displace it from protein binding.',
 'مضادات الالتهاب غير الستيرويدية تقلّل الإفراز الأنبوبي الكلوي للميثوتريكسات وتزيحه عن الارتباط البروتيني.',
 'Methotrexate accumulates, causing myelosuppression, mucositis and hepatotoxicity — often days after the dose.',
 'يتراكم الميثوتريكسات مسبّبًا تثبيط النخاع والتهاب الأغشية المخاطية وسمّية كبدية، وغالبًا بعد أيام من الجرعة.',
 'Avoid at anti-inflammatory NSAID doses. With low-dose weekly methotrexate and an unavoidable NSAID, check full blood count and liver function within two weeks.',
 'يُتجنّب مع الجرعات المضادة للالتهاب. وعند استخدام الميثوتريكسات الأسبوعي منخفض الجرعة مع ضرورة مضاد الالتهاب، تُفحص صورة الدم ووظائف الكبد خلال أسبوعين.',
 'Delayed', 'Established',
 'Phansalkar S et al. JAMIA 2012;19(5):735-743',
 'ONC/NLM high-priority DDI list', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

(gen_random_uuid(), 'Ingredient', 'methotrexate', 'Ingredient', 'trimethoprim', 'Contraindicated',
 'Both are folate antagonists, and trimethoprim additionally reduces methotrexate renal clearance.',
 'كلاهما مضاد للفولات، كما يقلّل التريميثوبريم إطراح الميثوتريكسات الكلوي.',
 'Severe, sometimes fatal bone-marrow suppression — reported even with low-dose weekly methotrexate.',
 'تثبيط شديد للنخاع العظمي قد يكون مميتًا، وقد سُجّل حتى مع الميثوتريكسات الأسبوعي منخفض الجرعة.',
 'Do not co-prescribe. Choose an antibiotic from another class — nitrofurantoin or an appropriate cephalosporin for urinary infection.',
 'لا يُوصفان معًا. يُختار مضاد حيوي من فئة أخرى مثل النيتروفورانتوين أو سيفالوسبورين مناسب لعدوى المسالك البولية.',
 'Delayed', 'Established',
 'Phansalkar S et al. JAMIA 2012;19(5):735-743',
 'ONC/NLM high-priority DDI list', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

-- ---- serotonergic combinations ---------------------------------------------------------------------------
(gen_random_uuid(), 'AtcClass', 'N06AB', 'Ingredient', 'tramadol', 'Major',
 'Additive serotonergic activity: SSRIs block reuptake and tramadol both inhibits reuptake and is a weak releaser.',
 'تأثير سيروتونيني تراكمي: مثبطات استرداد السيروتونين تمنع الاسترداد، والترامادول يثبّطه ويزيد إطلاقه.',
 'Serotonin syndrome — agitation, clonus, hyperthermia — and a lowered seizure threshold.',
 'متلازمة السيروتونين (هياج ورمع وارتفاع حرارة) مع خفض عتبة التشنّج.',
 'Prefer a non-serotonergic analgesic. If tramadol is necessary, use the lowest dose for the shortest time and counsel the patient on the warning signs.',
 'يُفضَّل مسكّن غير سيروتونيني. وإذا لزم الترامادول، تُستخدم أقل جرعة ولأقصر مدة مع توعية المريض بالعلامات التحذيرية.',
 'Rapid', 'Established',
 'Phansalkar S et al. JAMIA 2012;19(5):735-743',
 'ONC/NLM high-priority DDI list', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

(gen_random_uuid(), 'AtcClass', 'N06AB', 'Ingredient', 'linezolid', 'Contraindicated',
 'Linezolid is a reversible non-selective monoamine oxidase inhibitor.',
 'اللينزوليد مثبّط عكوس غير انتقائي لأكسيداز أحادي الأمين.',
 'Serotonin syndrome, which may be severe and rapid in onset.',
 'متلازمة السيروتونين، وقد تكون شديدة وسريعة الظهور.',
 'Do not co-prescribe. If linezolid is essential, stop the SSRI and allow a washout appropriate to its half-life, under specialist advice.',
 'لا يُوصفان معًا. وإذا كان اللينزوليد ضروريًا، يُوقف مثبط الاسترداد مع فترة غسل مناسبة لعمر النصف وباستشارة أخصائي.',
 'Rapid', 'Established',
 'Phansalkar S et al. JAMIA 2012;19(5):735-743',
 'ONC/NLM high-priority DDI list', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

-- ---- ACE inhibitors with potassium-sparing agents ---------------------------------------------------------
(gen_random_uuid(), 'AtcClass', 'C09A', 'Ingredient', 'spironolactone', 'Major',
 'Both reduce potassium excretion — ACE inhibition lowers aldosterone, and spironolactone blocks its receptor.',
 'كلاهما يقلّل إطراح البوتاسيوم: مثبط الإنزيم المحوّل يخفض الألدوستيرون، والسبيرونولاكتون يحصر مستقبله.',
 'Hyperkalaemia, which can reach cardiac-arrhythmia levels without symptoms beforehand.',
 'فرط بوتاسيوم الدم، وقد يبلغ مستويات مسبّبة لاضطراب النظم دون أعراض سابقة.',
 'The combination is appropriate in heart failure but must be monitored: check potassium and creatinine within one week of starting or of any dose change.',
 'الجمع مناسب في قصور القلب لكنه يتطلب متابعة: يُفحص البوتاسيوم والكرياتينين خلال أسبوع من البدء أو من أي تغيير في الجرعة.',
 'Delayed', 'Established',
 'Phansalkar S et al. JAMIA 2012;19(5):735-743',
 'ONC/NLM high-priority DDI list', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

(gen_random_uuid(), 'AtcClass', 'C09A', 'Ingredient', 'potassium chloride', 'Major',
 'Potassium supplementation added to reduced potassium excretion.',
 'إضافة مكمّل البوتاسيوم إلى انخفاض إطراحه.',
 'Hyperkalaemia with a risk of serious cardiac arrhythmia.',
 'فرط بوتاسيوم الدم مع خطر اضطراب نظم قلبي خطير.',
 'Avoid routine potassium supplements with an ACE inhibitor. Where a documented deficit exists, replace it with monitoring rather than empirically.',
 'تُتجنّب مكمّلات البوتاسيوم الروتينية مع مثبط الإنزيم المحوّل. وعند وجود نقص موثّق، يُعوَّض مع المتابعة لا تجريبيًا.',
 'Delayed', 'Established',
 'Phansalkar S et al. JAMIA 2012;19(5):735-743',
 'ONC/NLM high-priority DDI list', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

-- ---- QT prolongation --------------------------------------------------------------------------------------
(gen_random_uuid(), 'Ingredient', 'amiodarone', 'Ingredient', 'ciprofloxacin', 'Major',
 'Additive delay of cardiac repolarisation; both prolong the QT interval.',
 'تأخير تراكمي لإعادة الاستقطاب القلبي؛ كلاهما يطيل فترة QT.',
 'Torsades de pointes, particularly with hypokalaemia, hypomagnesaemia or bradycardia.',
 'اضطراب نظم من نوع تورساد دي بوانت، خاصة مع نقص البوتاسيوم أو المغنيسيوم أو بطء القلب.',
 'Use an antibiotic without QT effect where the infection allows. If unavoidable, correct potassium and magnesium first and obtain an ECG.',
 'يُستخدم مضاد حيوي بلا تأثير على QT إن سمحت العدوى. وإن تعذّر، يُصحَّح البوتاسيوم والمغنيسيوم أولًا مع تخطيط قلب.',
 'Rapid', 'Probable',
 'Phansalkar S et al. JAMIA 2012;19(5):735-743',
 'ONC/NLM high-priority DDI list', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true)
ON CONFLICT DO NOTHING;
