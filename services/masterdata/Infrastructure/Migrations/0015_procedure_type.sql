-- masterdata-service — 0015 procedure_type: the OP-Procedure kinds, as MASTER DATA.
--
-- ============================================================================================================
-- WHY A TABLE AND NOT AN ENUM
-- ============================================================================================================
-- 29.2 / design 45 §2 — "Type is master data, not an enum, administered like refill frequency: adding
-- 'Hydrotherapy' must be a data change, not a release." An enum would put a clinical vocabulary behind a
-- deployment, and the people who know that vocabulary are not the people who deploy.
--
-- ============================================================================================================
-- WHY is_session_based IS A FLAG AND NOT A NAME CHECK
-- ============================================================================================================
-- The composer reveals a "number of sessions" field when the SELECTED TYPE carries this flag. It must never
-- be `if (type === 'Physiotherapy')`: dialysis and rehabilitation are session-based too, so hard-coding the
-- name guarantees the same conversation twice more — and the second and third times it will be written as a
-- second and third special case rather than as this flag.
--
-- ============================================================================================================
-- WHY allowed_cpt_scopes EXISTS
-- ============================================================================================================
-- Each type declares which CPT sections (or explicit code ranges) it may accompany. A Physiotherapy type on a
-- minor-surgery code is a DATA ERROR and is refused 422 with a clear message. Left unvalidated the field is
-- decorative, and every report built on it is quietly wrong — which is worse than not having the field,
-- because the reports look right.
--
-- Stored as jsonb rather than a child table: it is a small, read-mostly declaration read in full every time,
-- never joined or aggregated, and a child table would buy referential integrity against CptSections — which
-- is code, not data, and so cannot be referenced anyway.

CREATE TABLE IF NOT EXISTS masterdata.procedure_type (
    code               varchar(32) PRIMARY KEY,
    name_en            text        NOT NULL,
    name_ar            text        NOT NULL,

    -- Drives the sessions field. Follows the FLAG, never the name.
    is_session_based   boolean     NOT NULL DEFAULT false,
    default_sessions   int         NULL CHECK (default_sessions IS NULL OR default_sessions > 0),
    max_sessions       int         NULL CHECK (max_sessions     IS NULL OR max_sessions     > 0),

    -- Which CPT sections this type may accompany, e.g. ["Medicine"] or ["Surgery","Medicine"].
    allowed_cpt_scopes jsonb       NOT NULL DEFAULT '[]'::jsonb,

    is_active          boolean     NOT NULL DEFAULT true,
    sort_order         int         NOT NULL DEFAULT 0,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),

    -- A non-session type must not carry session counts: they would be silently ignored by the composer and
    -- silently meaningful to anyone reading the table directly.
    CONSTRAINT ck_procedure_type_sessions_follow_the_flag CHECK (
        is_session_based OR (default_sessions IS NULL AND max_sessions IS NULL)),
    CONSTRAINT ck_procedure_type_default_within_max CHECK (
        default_sessions IS NULL OR max_sessions IS NULL OR default_sessions <= max_sessions),
    CONSTRAINT ck_procedure_type_scopes_is_array CHECK (jsonb_typeof(allowed_cpt_scopes) = 'array')
);

CREATE INDEX IF NOT EXISTS ix_procedure_type_active ON masterdata.procedure_type (is_active, sort_order);

-- ---- Seed (design 45 §2) -----------------------------------------------------------------------------------
-- Physiotherapy, Dialysis and Rehabilitation are session-based; the rest are not. Note that this is data:
-- adding Hydrotherapy is an INSERT, and it will reveal a sessions field without a line of code changing.

INSERT INTO masterdata.procedure_type
    (code, name_en, name_ar, is_session_based, default_sessions, max_sessions, allowed_cpt_scopes, sort_order)
VALUES
    ('Physiotherapy',       'Physiotherapy',        'العلاج الطبيعي',   true,  6,  30, '["Medicine"]', 10),
    ('MinorSurgery',        'Minor Surgery',        'جراحة صغرى',       false, NULL, NULL, '["Surgery"]', 20),
    ('InjectionInfusion',   'Injection / Infusion', 'حقن / تسريب',      false, NULL, NULL, '["Medicine"]', 30),
    ('Dialysis',            'Dialysis',             'غسيل كلوي',        true,  12, 156, '["Medicine"]', 40),
    ('WoundCare',           'Wound Care',           'العناية بالجروح',  false, NULL, NULL, '["Surgery","Medicine"]', 50),
    ('Rehabilitation',      'Rehabilitation',       'إعادة التأهيل',    true,  10,  60, '["Medicine"]', 60),
    ('DiagnosticProcedure', 'Diagnostic Procedure', 'إجراء تشخيصي',     false, NULL, NULL, '["Surgery","Medicine"]', 70),
    ('Other',               'Other',                'أخرى',             false, NULL, NULL, '["Surgery","Medicine"]', 900)
ON CONFLICT (code) DO NOTHING;
