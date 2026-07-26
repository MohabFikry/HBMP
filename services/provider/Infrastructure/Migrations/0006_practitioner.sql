-- provider-service — 0006 practitioners, specialty & branch assignment (phase 14.5, design 37 §4). ADDITIVE.
-- A practitioner is the clinical profile behind a user (logical FK to identity). Specialty is reference data;
-- a practitioner has one-or-many specialties (exactly one primary) and serves one-or-many branches. Psychiatry
-- and Clinical Psychology MUST be present — they drive the sensitivity defaults in 14.6.

CREATE TABLE IF NOT EXISTS provider.specialty (
    specialty_code varchar(12) PRIMARY KEY,
    name_en        text NOT NULL,
    name_ar        text NOT NULL,
    parent_code    varchar(12),
    is_deleted     boolean NOT NULL DEFAULT false
);

CREATE TABLE IF NOT EXISTS provider.practitioner (
    practitioner_id  uuid PRIMARY KEY,
    tenant_id        text NOT NULL,
    user_id          text NOT NULL,
    practitioner_type varchar(10) NOT NULL CHECK (practitioner_type IN ('Doctor','Nurse')),
    full_name_en     text NOT NULL,
    full_name_ar     text NOT NULL,
    license_no       text,
    license_expiry   date,
    status           varchar(12) NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Suspended','Inactive')),
    is_deleted       boolean NOT NULL DEFAULT false,
    row_version      integer NOT NULL DEFAULT 0,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now()
);
-- One practitioner profile per user (while live).
CREATE UNIQUE INDEX IF NOT EXISTS ux_practitioner_user ON provider.practitioner (user_id) WHERE is_deleted = false;

CREATE TABLE IF NOT EXISTS provider.practitioner_specialty (
    practitioner_id uuid NOT NULL REFERENCES provider.practitioner (practitioner_id),
    specialty_code  varchar(12) NOT NULL REFERENCES provider.specialty (specialty_code),
    is_primary      boolean NOT NULL DEFAULT false,
    PRIMARY KEY (practitioner_id, specialty_code)
);
-- Exactly one primary specialty per practitioner.
CREATE UNIQUE INDEX IF NOT EXISTS ux_practitioner_primary_specialty
    ON provider.practitioner_specialty (practitioner_id) WHERE is_primary;

CREATE TABLE IF NOT EXISTS provider.practitioner_branch_assignment (
    assignment_id   uuid PRIMARY KEY,
    practitioner_id uuid NOT NULL REFERENCES provider.practitioner (practitioner_id),
    branch_id       uuid NOT NULL,
    valid_from      date NOT NULL,
    valid_to        date,
    status          varchar(10) NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Revoked'))
);
CREATE INDEX IF NOT EXISTS ix_pba_practitioner ON provider.practitioner_branch_assignment (practitioner_id, status);
CREATE INDEX IF NOT EXISTS ix_pba_branch       ON provider.practitioner_branch_assignment (branch_id);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON provider.specialty, provider.practitioner,
            provider.practitioner_specialty, provider.practitioner_branch_assignment TO hbmp_app;
    END IF;
END $$;

-- Seed the specialty reference set (design 37 §4) idempotently.
INSERT INTO provider.specialty (specialty_code, name_en, name_ar) VALUES
    ('GP',     'General Practice',         'الممارسة العامة'),
    ('IM',     'Internal Medicine',        'الباطنة'),
    ('PED',    'Pediatrics',               'طب الأطفال'),
    ('OBGYN',  'Obstetrics & Gynaecology', 'النساء والتوليد'),
    ('CARD',   'Cardiology',               'أمراض القلب'),
    ('DERM',   'Dermatology',              'الجلدية'),
    ('PSYCH',  'Psychiatry',               'الطب النفسي'),
    ('CPSY',   'Clinical Psychology',      'علم النفس الإكلينيكي'),
    ('NEURO',  'Neurology',                'المخ والأعصاب'),
    ('ORTHO',  'Orthopaedics',             'العظام'),
    ('ENT',    'ENT',                      'الأنف والأذن والحنجرة'),
    ('OPHTH',  'Ophthalmology',            'الرمد'),
    ('ENDO',   'Endocrinology',            'الغدد الصماء'),
    ('GASTRO', 'Gastroenterology',         'الجهاز الهضمي'),
    ('NEPH',   'Nephrology',               'الكلى'),
    ('PULM',   'Pulmonology',              'الصدر'),
    ('URO',    'Urology',                  'المسالك البولية'),
    ('ONC',    'Oncology',                 'الأورام'),
    ('RHEUM',  'Rheumatology',             'الروماتيزم'),
    ('GSURG',  'General Surgery',          'الجراحة العامة'),
    ('EM',     'Emergency Medicine',       'طب الطوارئ'),
    ('RAD',    'Radiology',                'الأشعة'),
    ('PATH',   'Pathology',                'الباثولوجيا'),
    ('PHYSIO', 'Physiotherapy',            'العلاج الطبيعي'),
    ('NUTR',   'Nutrition',                'التغذية'),
    ('DENT',   'Dentistry',                'طب الأسنان')
ON CONFLICT (specialty_code) DO NOTHING;
