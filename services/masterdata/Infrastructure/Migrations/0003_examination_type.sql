-- masterdata-service — 0003 examination type + sensitivity (phase 14.6, design 37 §5). ADDITIVE.
-- Reference data whose sensitivity is denormalized onto orders/results so read-time gating never needs a
-- cross-service join. MENTAL-HEALTH assessments/consultations are seeded Sensitive + MentalHealth (the
-- confirmed requirement); the other special categories are left for the Medical Director + DPO to ratify as
-- configuration — no policy is hard-coded in code.

CREATE TABLE IF NOT EXISTS masterdata.examination_type (
    examination_type_id uuid PRIMARY KEY,
    code                varchar(24) NOT NULL,
    name_en             text NOT NULL,
    name_ar             text NOT NULL,
    category            varchar(16) NOT NULL CHECK (category IN ('Lab','Imaging','Procedure','Consultation','Assessment')),
    default_code_system varchar(8)  NOT NULL DEFAULT 'CPT' CHECK (default_code_system IN ('CPT','LOINC','LOCAL')),
    default_code        text,
    sensitivity_level   varchar(16) NOT NULL DEFAULT 'Standard' CHECK (sensitivity_level IN ('Standard','Sensitive','HighlySensitive')),
    sensitive_category  varchar(20) CHECK (sensitive_category IN ('MentalHealth','HivSti','Genetic','SubstanceUse','ReproductiveHealth','GbvForensic','Other')),
    status              varchar(12) NOT NULL DEFAULT 'Active'
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_examination_type_code ON masterdata.examination_type (code);
CREATE INDEX IF NOT EXISTS ix_examination_type_sensitivity ON masterdata.examination_type (sensitivity_level);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE ON masterdata.examination_type TO hbmp_app;
    END IF;
END $$;

-- Starter set. Mental-health assessments/consultations → Sensitive + MentalHealth (confirmed requirement).
INSERT INTO masterdata.examination_type
    (examination_type_id, code, name_en, name_ar, category, default_code_system, default_code, sensitivity_level, sensitive_category) VALUES
    ('0190c100-0000-7000-8000-000000000001', 'CBC',        'Complete Blood Count',        'صورة دم كاملة',        'Lab',          'LOINC', '58410-2', 'Standard',  NULL),
    ('0190c100-0000-7000-8000-000000000002', 'LIPID',      'Lipid Panel',                 'دهون الدم',            'Lab',          'LOINC', '57698-3', 'Standard',  NULL),
    ('0190c100-0000-7000-8000-000000000003', 'CXR',        'Chest X-Ray',                 'أشعة صدر',             'Imaging',      'CPT',   '71046',   'Standard',  NULL),
    ('0190c100-0000-7000-8000-000000000004', 'MRI_BRAIN',  'MRI Brain',                   'رنين المخ',            'Imaging',      'CPT',   '70551',   'Standard',  NULL),
    ('0190c100-0000-7000-8000-000000000005', 'GP_CONSULT', 'General Consultation',        'كشف عام',              'Consultation', 'CPT',   '99213',   'Standard',  NULL),
    ('0190c100-0000-7000-8000-000000000010', 'PSYCH_ASSESS','Psychiatric Assessment',     'تقييم نفسي',           'Assessment',   'CPT',   '90791',   'Sensitive', 'MentalHealth'),
    ('0190c100-0000-7000-8000-000000000011', 'PSYCH_CONSULT','Psychiatry Consultation',   'استشارة نفسية',        'Consultation', 'CPT',   '90792',   'Sensitive', 'MentalHealth'),
    ('0190c100-0000-7000-8000-000000000012', 'PSYCHOTHERAPY','Psychotherapy Session',     'جلسة علاج نفسي',       'Procedure',    'CPT',   '90837',   'Sensitive', 'MentalHealth')
ON CONFLICT (code) DO NOTHING;
