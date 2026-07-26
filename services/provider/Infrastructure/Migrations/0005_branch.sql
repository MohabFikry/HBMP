-- provider-service — 0005 internal Mersal branch (phase 14.1, design 37 §2). ADDITIVE / backward-compatible.
-- provider-service now covers "network & facilities": the contracted network (0001–0004) AND Mersal's own
-- branches. A branch is an INTERNAL org unit and is deliberately NOT provider_location (a contracted site).
-- Branch is slow-changing org reference data with NO PHI and NO tenant/provider scope (the six branches are
-- shared), so — unlike the provider tables — it carries no RLS predicate. Soft-delete + row_version keep it
-- auditable; it is never hard-deleted.

CREATE TABLE IF NOT EXISTS provider.branch (
    branch_id     uuid PRIMARY KEY,
    branch_code   varchar(8)  NOT NULL,
    name_en       text        NOT NULL,
    name_ar       text        NOT NULL,
    city          text,
    address       text,
    timezone      text        NOT NULL DEFAULT 'Africa/Cairo',
    phone         text,
    opening_hours jsonb,
    status        varchar(12) NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Suspended','Closed')),
    is_deleted    boolean     NOT NULL DEFAULT false,
    row_version   integer     NOT NULL DEFAULT 0,
    created_by    text,
    updated_by    text,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now()
);

-- Business-key uniqueness holds only for live rows (soft-delete convention proven across the platform).
CREATE UNIQUE INDEX IF NOT EXISTS ux_branch_code ON provider.branch (branch_code) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS ix_branch_status ON provider.branch (status);

-- The app role (NOBYPASSRLS hbmp_app, provisioned in 0004) needs DML on the new table. Default privileges
-- from 0004 cover owner-created tables, but grant explicitly so the migration is self-contained.
GRANT SELECT, INSERT, UPDATE, DELETE ON provider.branch TO hbmp_app;

-- Seed the six Mersal branches idempotently (37 §2.1). Fixed v7-shaped UUIDs keep the reference data stable
-- across environments. Re-running is a no-op (conflict on the live branch_code index).
INSERT INTO provider.branch (branch_id, branch_code, name_en, name_ar, city) VALUES
    ('0190b100-0000-7000-8000-000000000001', 'ASW', 'Aswan',          'أسوان',              'Aswan'),
    ('0190b100-0000-7000-8000-000000000002', 'ALX', 'Alexandria',     'الإسكندرية',         'Alexandria'),
    ('0190b100-0000-7000-8000-000000000003', 'OCT', '6th of October', 'السادس من أكتوبر',   'Giza'),
    ('0190b100-0000-7000-8000-000000000004', 'MAA', 'Maadi',          'المعادي',            'Cairo'),
    ('0190b100-0000-7000-8000-000000000005', 'DOK', 'Dokki',          'الدقي',              'Giza'),
    ('0190b100-0000-7000-8000-000000000006', 'NSR', 'Nasr City',      'مدينة نصر',          'Cairo')
ON CONFLICT (branch_code) WHERE is_deleted = false DO NOTHING;
