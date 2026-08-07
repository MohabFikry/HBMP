-- pharmacy-service — 0012 acute / chronic prescriptions and their refill windows.
--
-- ============================================================================================================
-- 29.5 / design 45 §5 · window model: docs/superpowers/specs/2026-08-07-chronic-refill-windows-design.md
-- ============================================================================================================

-- ---- refill_frequency: MASTER DATA, supervisor-configurable, NOT an enum ------------------------------------
--
-- "Configurable by the Approval Supervisor, so it is a master table, not an enum. Adding 'every 6 months'
-- must be a data change, not a release." An enum would put a dispensing cadence behind a deployment, and the
-- people who set that cadence are not the people who deploy.

CREATE TABLE IF NOT EXISTS pharmacy.refill_frequency (
    code       varchar(32) PRIMARY KEY,
    months     int         NOT NULL CHECK (months > 0),
    name_en    text        NOT NULL,
    name_ar    text        NOT NULL,
    is_active  boolean     NOT NULL DEFAULT true,
    sort_order int         NOT NULL DEFAULT 0
);

INSERT INTO pharmacy.refill_frequency (code, months, name_en, name_ar, sort_order) VALUES
    ('Monthly',       1, 'Monthly',        'شهرياً',        10),
    ('Every2Months',  2, 'Every 2 months', 'كل شهرين',      20),
    ('Every3Months',  3, 'Every 3 months', 'كل 3 أشهر',     30)
ON CONFLICT (code) DO NOTHING;

-- ---- prescription: acute or chronic --------------------------------------------------------------------------

ALTER TABLE pharmacy.prescription
    ADD COLUMN IF NOT EXISTS kind                  varchar(16) NOT NULL DEFAULT 'Acute',
    ADD COLUMN IF NOT EXISTS refill_frequency_code varchar(32) NULL REFERENCES pharmacy.refill_frequency(code),
    ADD COLUMN IF NOT EXISTS duration_days         int         NULL,
    ADD COLUMN IF NOT EXISTS valid_from            date        NULL,
    ADD COLUMN IF NOT EXISTS valid_until           date        NULL;

ALTER TABLE pharmacy.prescription DROP CONSTRAINT IF EXISTS ck_prescription_kind;
ALTER TABLE pharmacy.prescription
    ADD CONSTRAINT ck_prescription_kind CHECK (kind IN ('Acute','Chronic'));

-- "Chronic requires a duration greater than one month. A 14-day course is not chronic; reject with a clear
-- message rather than silently accepting." The API returns the clear message; this is the backstop, because
-- a chronic script with no frequency has no windows and would be undispensable in a way nothing reports.
ALTER TABLE pharmacy.prescription DROP CONSTRAINT IF EXISTS ck_prescription_chronic_requires_schedule;
ALTER TABLE pharmacy.prescription
    ADD CONSTRAINT ck_prescription_chronic_requires_schedule CHECK (
        kind <> 'Chronic'
        OR (refill_frequency_code IS NOT NULL AND duration_days IS NOT NULL AND duration_days > 30));

-- An ACUTE script carries no refill schedule. Allowing one would make "is this chronic?" answerable two ways.
ALTER TABLE pharmacy.prescription DROP CONSTRAINT IF EXISTS ck_prescription_acute_has_no_schedule;
ALTER TABLE pharmacy.prescription
    ADD CONSTRAINT ck_prescription_acute_has_no_schedule CHECK (
        kind <> 'Acute' OR refill_frequency_code IS NULL);

ALTER TABLE pharmacy.prescription DROP CONSTRAINT IF EXISTS ck_prescription_validity_ordered;
ALTER TABLE pharmacy.prescription
    ADD CONSTRAINT ck_prescription_validity_ordered CHECK (
        valid_from IS NULL OR valid_until IS NULL OR valid_until >= valid_from);

-- ---- prescription_dispense_window ------------------------------------------------------------------------------
--
-- PER LINE, not per prescription: "lines can have different durations", so one script's amlodipine and its
-- metformin can be on different schedules and a window keyed to the script could not express that.
--
-- STATUS IS STORED FOR Blocked AND Missed, AND ONLY FOR THOSE.
--   * `Blocked` records that a named pharmacist presented a real beneficiary and eligibility said no. That is
--     an EVENT, not a function of dates, and it cannot be derived from anything.
--   * `Missed` is a forfeiture — money that will now never be claimed — so it needs a timestamp and must be
--     idempotent.
--   * `Open` is NEVER WRITTEN. Dispensability is computed from opens_at/closes_at at read time, so a stalled
--     sweeper delays a forfeiture but can never prevent a collection. The counter enforces; the sweeper
--     records. See the design note for why that split is the whole point.

CREATE TABLE IF NOT EXISTS pharmacy.prescription_dispense_window (
    window_id            uuid PRIMARY KEY,
    tenant_id            text        NOT NULL,
    prescription_id      uuid        NOT NULL REFERENCES pharmacy.prescription(prescription_id),
    prescription_line_id uuid        NOT NULL REFERENCES pharmacy.prescription_line(prescription_line_id),
    window_no            int         NOT NULL CHECK (window_no >= 1),

    scheduled_open_date  date        NOT NULL,
    -- scheduled_open_date − early tolerance. STORED rather than computed at read time: the tolerance is
    -- configurable, and a window issued under a 5-day tolerance must keep it if the setting later changes.
    opens_at             date        NOT NULL,
    closes_at            date        NOT NULL,

    allocated_quantity   numeric(14,3) NOT NULL CHECK (allocated_quantity >= 0),
    dispensed_quantity   numeric(14,3) NOT NULL DEFAULT 0 CHECK (dispensed_quantity >= 0),

    status               varchar(20) NOT NULL DEFAULT 'Pending'
        CHECK (status IN ('Pending','Open','Dispensed','PartiallyDispensed','Missed','Blocked')),
    blocked_reason       text        NULL,
    missed_at            timestamptz NULL,

    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT uq_window_per_line UNIQUE (prescription_line_id, window_no),
    CONSTRAINT ck_window_dates_ordered CHECK (opens_at <= scheduled_open_date AND scheduled_open_date <= closes_at),
    -- A window can never hand over more than it was allocated. The per-dispense consume enforces this too;
    -- the CHECK is what makes it true of the ROW rather than only of the path that wrote it.
    CONSTRAINT ck_window_not_over_dispensed CHECK (dispensed_quantity <= allocated_quantity),
    -- A blocked window says WHY. "Blocked is not the patient's doing and must be visible to the case team" —
    -- and a block with no reason is not visible, it is merely a stuck row.
    CONSTRAINT ck_window_blocked_has_reason CHECK (status <> 'Blocked' OR blocked_reason IS NOT NULL),
    -- A forfeiture has a time. Without it, "when did this become missed, and had coverage already lapsed by
    -- then?" is unanswerable.
    CONSTRAINT ck_window_missed_has_time CHECK (status <> 'Missed' OR missed_at IS NOT NULL)
);

CREATE INDEX IF NOT EXISTS ix_window_line ON pharmacy.prescription_dispense_window (prescription_line_id, window_no);
-- The sweeper's query: windows past their close that nobody has collected. Partial, because a swept row is
-- never swept again and indexing the settled ones would be indexing the answer "no".
CREATE INDEX IF NOT EXISTS ix_window_sweepable
    ON pharmacy.prescription_dispense_window (closes_at)
    WHERE status IN ('Pending','Open') AND dispensed_quantity = 0;

-- ---- Early tolerance, as configuration -----------------------------------------------------------------------
-- Default 5 days (design 45 §5). Held in system_config beside the other validity settings rather than as a
-- constant, because it is a dispensing-policy number the Approval Supervisor owns.
INSERT INTO pharmacy.refill_frequency (code, months, name_en, name_ar, is_active, sort_order)
SELECT 'Every6Months', 6, 'Every 6 months', 'كل 6 أشهر', false, 40
WHERE NOT EXISTS (SELECT 1 FROM pharmacy.refill_frequency WHERE code = 'Every6Months');
-- Seeded INACTIVE on purpose: it demonstrates that adding a frequency is an INSERT rather than a release,
-- without silently offering prescribers a cadence nobody has approved.

-- ---- RLS on the new table --------------------------------------------------------------------------------
--
-- 29.5 — the same tenant isolation every other pharmacy table carries (ADR-0011). A refill window names a
-- beneficiary's medicine schedule, so a table with a tenant_id and no policy is not "not yet secured": under
-- the hbmp_app NOBYPASSRLS role it is a table every tenant can read. Caught by
-- Mersal.Architecture.Tests.HousePatternTests.Every_tenant_scoped_table_has_an_rls_policy, which exists
-- because this is easy to forget and invisible until someone looks.

GRANT SELECT, INSERT, UPDATE, DELETE ON pharmacy.prescription_dispense_window TO hbmp_app;

ALTER TABLE pharmacy.prescription_dispense_window ENABLE ROW LEVEL SECURITY;
ALTER TABLE pharmacy.prescription_dispense_window FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_prescription_dispense_window ON pharmacy.prescription_dispense_window;
CREATE POLICY rls_prescription_dispense_window ON pharmacy.prescription_dispense_window
    USING (tenant_id = current_setting('app.tenant_id', true));
