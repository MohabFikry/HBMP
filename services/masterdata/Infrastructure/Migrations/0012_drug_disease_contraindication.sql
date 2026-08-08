-- masterdata-service — 0012 drug–disease contraindications (44-clinical-validation-hardening §5,
-- phase 28 Gate 9). Idempotent.
--
-- ============================================================================================================
-- INDICATION MISMATCH AND CONTRAINDICATION ARE TWO DIFFERENT CHECKS, AND CONFLATING THEM IS WHY ONE IS NOISE
-- ============================================================================================================
-- The request that started this work said "compatibility of the medication with diagnosis". There are two
-- questions hiding in that phrase:
--
--   INDICATION MISMATCH — is this drug USED FOR this condition? A mismatch means off-label, which is
--                         legitimate and common, so the warning fires constantly and is dismissed constantly.
--   CONTRAINDICATION    — is this drug DANGEROUS IN this condition? A hit means potential harm.
--
-- Phase 26 built the first and not the second. This is the one with the clinical value.
--
-- Keyed to the ICD hierarchy from 0011, so a rule written at a block or a category catches every specific
-- code underneath it — "NSAIDs in peptic ulcer disease" is K25-K27, not an enumeration of subcategories that
-- would need extending every time a coder is more precise than usual.

CREATE TABLE IF NOT EXISTS masterdata.drug_disease_contraindication (
    rule_id          uuid PRIMARY KEY,

    -- The drug side, same vocabulary as interaction_rule: a molecule or a whole ATC class. "All NSAIDs" is
    -- one row and survives new products entering the market.
    subject_kind     text NOT NULL CHECK (subject_kind IN ('Ingredient','AtcClass')),
    subject_value    text NOT NULL,

    -- The condition side: an ICD-10 node. Matching is descendant-or-self against masterdata.icd_ancestor.
    icd_scope        text NOT NULL,

    severity         text NOT NULL CHECK (severity IN ('Minor','Moderate','Major','Contraindicated')),

    -- Why it is hazardous, what happens, and what to do instead (design 44 §3, §5). The management line is
    -- the one most likely to change the prescription, so it is NOT NULL: a contraindication that says only
    -- "avoid" leaves the prescriber with a patient still in pain and no alternative.
    mechanism_en     text NOT NULL,
    mechanism_ar     text NOT NULL,
    clinical_effect_en text NOT NULL,
    clinical_effect_ar text NOT NULL,
    management_en    text NOT NULL,
    management_ar    text NOT NULL,

    evidence_level   text NOT NULL CHECK (evidence_level IN ('Established','Probable','Theoretical')),
    citation         text NOT NULL,
    source           text NOT NULL,
    source_release   varchar(64),
    reviewed_by      text,
    reviewed_at      timestamptz,
    is_active        boolean NOT NULL DEFAULT false,

    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),

    -- Same governance constraint as interaction_rule and the allergen mappings: nothing warns a prescriber
    -- without a named pharmacist behind it.
    CONSTRAINT ck_contraindication_reviewed
        CHECK (NOT is_active OR (reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_contraindication_rule
    ON masterdata.drug_disease_contraindication (subject_kind, subject_value, icd_scope);
CREATE INDEX IF NOT EXISTS ix_contraindication_subject
    ON masterdata.drug_disease_contraindication (subject_kind, subject_value) WHERE is_active;
CREATE INDEX IF NOT EXISTS ix_contraindication_scope
    ON masterdata.drug_disease_contraindication (icd_scope) WHERE is_active;

-- ============================================================================================================
-- SEED — the classic pairs, pharmacist-reviewed with citations (design 44 §5).
-- ============================================================================================================
-- PREGNANCY IS DELIBERATELY NOT IN THIS TABLE. It is a STATUS, not a coded diagnosis on the encounter, and
-- modelling it as an ICD scope would mean it only ever fired for a patient someone had coded O00-O9A on the
-- visit — which is exactly the patient nobody needs reminding about. It travels as structured patient context
-- instead (emr.pregnancy_status), and the rules that depend on it carry `icd_scope = 'PREGNANCY'`, a sentinel
-- the matcher resolves against that status rather than against the diagnosis list.

INSERT INTO masterdata.ingredient (ingredient_id, ingredient_key, name_en, name_ar, atc_code, source, source_release)
VALUES
    (gen_random_uuid(), 'doxycycline',   'Doxycycline',   'دوكسيسيكلين',  'J01AA02', 'phase-28 curated', 'seed-v1'),
    (gen_random_uuid(), 'propranolol',   'Propranolol',   'بروبرانولول',  'C07AA05', 'phase-28 curated', 'seed-v1')
ON CONFLICT (ingredient_key) DO NOTHING;

INSERT INTO masterdata.drug_disease_contraindication (
    rule_id, subject_kind, subject_value, icd_scope, severity,
    mechanism_en, mechanism_ar, clinical_effect_en, clinical_effect_ar, management_en, management_ar,
    evidence_level, citation, source, source_release, reviewed_by, reviewed_at, is_active)
VALUES
-- ---- NSAIDs, the highest-volume prescribing class with the widest contraindication set -------------------
(gen_random_uuid(), 'AtcClass', 'M01A', 'K27', 'Contraindicated',
 'NSAIDs inhibit COX-1 and remove the prostaglandin-mediated protection of the gastric mucosa.',
 'مضادات الالتهاب غير الستيرويدية تثبّط COX-1 وتلغي الحماية المعتمدة على البروستاجلاندين للغشاء المخاطي المعدي.',
 'Re-bleeding or perforation of an existing peptic ulcer, which can be fatal and often gives no warning pain.',
 'نزف متكرر أو انثقاب لقرحة هضمية قائمة، وقد يكون مميتًا وغالبًا دون ألم منذر.',
 'Do not prescribe. Use paracetamol for analgesia. If an anti-inflammatory is unavoidable, it needs specialist advice and gastroprotection.',
 'لا يُوصف. يُستخدم الباراسيتامول للألم. وإذا تعذّر تجنّب مضاد الالتهاب فيلزم استشارة أخصائي مع وقاية معدية.',
 'Established',
 'NICE CG184, Gastro-oesophageal reflux disease and dyspepsia in adults; BNF 87, NSAIDs — cautions and contraindications',
 'phase-28 pharmacist review', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

(gen_random_uuid(), 'AtcClass', 'M01A', 'N18', 'Major',
 'NSAIDs remove the prostaglandin-mediated afferent arteriolar dilation that maintains glomerular filtration when renal perfusion is already reduced.',
 'مضادات الالتهاب غير الستيرويدية تلغي التوسّع الشرياني الوارد الذي يحافظ على الترشيح الكبيبي عند انخفاض التروية الكلوية.',
 'Acute-on-chronic kidney injury and hyperkalaemia; repeated courses accelerate the loss of remaining function.',
 'إصابة كلوية حادة على مزمنة وفرط بوتاسيوم الدم، والدورات المتكررة تسرّع فقدان ما تبقى من وظيفة.',
 'Avoid. Use paracetamol. If unavoidable, keep the course to a few days, ensure hydration, and check creatinine and potassium within a week.',
 'يُتجنّب ويُستخدم الباراسيتامول. وإن تعذّر، تُقصَّر المدة لأيام قليلة مع ضمان الإماهة وفحص الكرياتينين والبوتاسيوم خلال أسبوع.',
 'Established',
 'KDIGO 2024 Clinical Practice Guideline for CKD; BNF 87, NSAIDs — renal impairment',
 'phase-28 pharmacist review', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

(gen_random_uuid(), 'AtcClass', 'M01A', 'I50', 'Major',
 'NSAIDs cause sodium and water retention and blunt the effect of diuretics and ACE inhibitors.',
 'مضادات الالتهاب غير الستيرويدية تسبّب احتباس الصوديوم والماء وتضعف أثر المدرّات ومثبطات الإنزيم المحوّل.',
 'Decompensation of heart failure — increasing oedema, breathlessness and hospital admission.',
 'تفاقم قصور القلب مع زيادة الوذمة وضيق النفس ودخول المستشفى.',
 'Avoid. Use paracetamol. If an NSAID has already been started, review weight and breathlessness within a week.',
 'يُتجنّب ويُستخدم الباراسيتامول. وإن كان قد بُدئ فعلًا، تُراجَع الوزن وضيق النفس خلال أسبوع.',
 'Established',
 'ESC 2021 Guidelines for the diagnosis and treatment of acute and chronic heart failure; BNF 87',
 'phase-28 pharmacist review', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

-- ---- beta-blockers in asthma ------------------------------------------------------------------------------
(gen_random_uuid(), 'AtcClass', 'C07AA', 'J45', 'Contraindicated',
 'Non-selective beta-blockade antagonises beta-2 mediated bronchodilation in the airway.',
 'حصر بيتا غير الانتقائي يعاكس التوسّع القصبي المعتمد على مستقبلات بيتا-٢ في المجرى الهوائي.',
 'Severe bronchospasm, which may not respond fully to a salbutamol rescue because the receptor is blocked.',
 'تشنّج قصبي شديد قد لا يستجيب تمامًا للسالبوتامول لأن المستقبل محصور.',
 'Do not prescribe a non-selective beta-blocker. Where beta-blockade is genuinely required, a cardioselective agent (bisoprolol, metoprolol) at the lowest effective dose is the option to discuss with a specialist.',
 'لا يُوصف حاصر بيتا غير انتقائي. وعند الحاجة الحقيقية لحصر بيتا، يُناقَش مع أخصائي استخدام حاصر انتقائي للقلب (بيسوبرولول، ميتوبرولول) بأقل جرعة فعالة.',
 'Established',
 'GINA 2024 Global Strategy for Asthma Management and Prevention; BNF 87, beta-adrenoceptor blocking drugs',
 'phase-28 pharmacist review', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

-- ---- metformin in significant renal impairment ------------------------------------------------------------
(gen_random_uuid(), 'Ingredient', 'metformin', 'N18', 'Major',
 'Metformin is cleared unchanged by the kidney; impaired clearance allows it to accumulate.',
 'الميتفورمين يُطرح دون تغيير عبر الكلى، وضعف الإطراح يسمح بتراكمه.',
 'Lactic acidosis — uncommon, but with a high mortality when it occurs.',
 'حماض لبني — غير شائع لكنه عالي الوفيات عند حدوثه.',
 'Review the dose against renal function: reduce below eGFR 45 and stop below 30. Hold during any acute illness causing dehydration, and before contrast imaging.',
 'تُراجَع الجرعة حسب وظائف الكلى: تُخفَّض تحت ٤٥ وتُوقف تحت ٣٠ لمعدل الترشيح، وتُوقف مؤقتًا في أي مرض حاد مسبّب للجفاف وقبل التصوير بالصبغة.',
 'Established',
 'NICE NG28, Type 2 diabetes in adults; BNF 87, metformin — renal impairment',
 'phase-28 pharmacist review', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

-- ---- pregnancy, against the STATUS rather than a coded diagnosis -----------------------------------------
(gen_random_uuid(), 'AtcClass', 'C09', 'PREGNANCY', 'Contraindicated',
 'ACE inhibitors and angiotensin receptor blockers suppress the fetal renin-angiotensin system, which the fetal kidney depends on.',
 'مثبطات الإنزيم المحوّل وحاصرات مستقبل الأنجيوتنسين تثبّط نظام الرينين-أنجيوتنسين الجنيني الذي تعتمد عليه كلية الجنين.',
 'Fetal renal failure, oligohydramnios, skull hypoplasia and death — in the second and third trimesters especially.',
 'فشل كلوي جنيني وقلة السائل الأمنيوسي ونقص تنسّج الجمجمة والوفاة، خاصة في الثلثين الثاني والثالث.',
 'Stop immediately and switch. Labetalol, nifedipine or methyldopa are the agents used in pregnancy. A woman of childbearing age started on one of these should be counselled before she conceives, not after.',
 'يُوقف فورًا ويُستبدل. تُستخدم في الحمل: لابيتالول أو نيفيديبين أو ميثيل دوبا. والمرأة في سن الإنجاب تُنصح قبل الحمل لا بعده.',
 'Established',
 'NICE NG133, Hypertension in pregnancy; FDA labelling for ACE inhibitors, boxed warning — fetal toxicity',
 'phase-28 pharmacist review', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

(gen_random_uuid(), 'AtcClass', 'J01A', 'PREGNANCY', 'Contraindicated',
 'Tetracyclines chelate calcium and are deposited in developing bone and tooth enamel.',
 'التتراسيكلينات ترتبط بالكالسيوم وتترسّب في العظم والمينا أثناء النمو.',
 'Permanent discolouration of the child''s teeth and impaired bone growth; maternal hepatotoxicity at high doses.',
 'تلوّن دائم لأسنان الطفل وضعف نمو العظام، وسمّية كبدية للأم عند الجرعات العالية.',
 'Do not prescribe. Amoxicillin, erythromycin or nitrofurantoin (avoiding term) cover most indications in pregnancy.',
 'لا يُوصف. الأموكسيسيلين أو الإريثروميسين أو النيتروفورانتوين (مع تجنّبه قرب الولادة) يغطي معظم الدواعي في الحمل.',
 'Established',
 'BNF 87, tetracyclines — pregnancy; WHO Model List of Essential Medicines, antibiotic use in pregnancy',
 'phase-28 pharmacist review', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true),

(gen_random_uuid(), 'AtcClass', 'J01M', 'PREGNANCY', 'Major',
 'Fluoroquinolones affect developing cartilage in animal studies and carry a tendon-toxicity signal in humans.',
 'الفلوروكينولونات تؤثر على الغضروف النامي في الدراسات الحيوانية وتحمل إشارة سمّية وترية لدى البشر.',
 'Uncertain fetal cartilage risk, and maternal tendinopathy.',
 'خطر غضروفي جنيني غير مؤكد، واعتلال وتري لدى الأم.',
 'Reserve for infections with no safer alternative, on specialist advice. Amoxicillin, cefalexin or nitrofurantoin cover the common indications.',
 'يُحفظ للعدوى التي لا بديل أكثر أمانًا لها وباستشارة أخصائي. الأموكسيسيلين أو السيفالكسين أو النيتروفورانتوين يغطي الدواعي الشائعة.',
 'Probable',
 'BNF 87, quinolones — pregnancy; EMA 2018 review of systemic fluoroquinolones',
 'phase-28 pharmacist review', 'seed-v1', 'Mersal clinical pharmacy (phase 28)', now(), true)
ON CONFLICT DO NOTHING;
