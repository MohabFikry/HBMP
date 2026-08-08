-- patient-service — 0004 the operational registration record.
--
-- Registration captured six fields; the desk works from a card number, a case file, a nationality, a plan and
-- six standing notes. Those lived in a spreadsheet beside the system, which is why the registry could not
-- answer questions the desk is asked every day.
--
-- Additive and idempotent, per the expand/contract rule: every column is nullable, no backfill runs, and an
-- older build keeps working against this schema because nothing it writes has become invalid.

-- ── 1) The person ───────────────────────────────────────────────────────────────────────────────────────────
--
-- card_number is NOT member_no. member_no (MRS-M-YYYY-NNNNNN) is issued by this service at activation; the
-- card number is printed on the card already in the beneficiary's hand at the desk, before anybody has
-- approved their application. Collapsing them would mean either issuing a member number to an unapproved
-- person or refusing to record the card they are holding.
ALTER TABLE patient.beneficiary ADD COLUMN IF NOT EXISTS card_number   text;
ALTER TABLE patient.beneficiary ADD COLUMN IF NOT EXISTS middle_name   text;
ALTER TABLE patient.beneficiary ADD COLUMN IF NOT EXISTS individual_no text;
ALTER TABLE patient.beneficiary ADD COLUMN IF NOT EXISTS case_no       text;

-- A birth date transcribed from an incomplete refugee document is still worth storing — an age-banded
-- eligibility rule needs SOMETHING — but nothing downstream may present it as exact, and a report keyed on
-- birthdays has to be able to exclude it. A flag beside the date says so; a NULL date could not.
ALTER TABLE patient.beneficiary ADD COLUMN IF NOT EXISTS birth_date_is_approximate boolean NOT NULL DEFAULT false;

-- Two people on one card is a benefit leak: the second person's consumption lands on the first one's limits.
-- Partial, so a soft-deleted record never blocks re-issuing its card number to the person it belonged to.
CREATE UNIQUE INDEX IF NOT EXISTS uq_beneficiary_card_number_active
    ON patient.beneficiary (card_number)
    WHERE is_deleted = false AND card_number IS NOT NULL;

-- Both are how the desk and the case team find a person when no identifier is to hand.
CREATE INDEX IF NOT EXISTS ix_beneficiary_individual_no ON patient.beneficiary (individual_no)
    WHERE individual_no IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_beneficiary_case_no ON patient.beneficiary (case_no)
    WHERE case_no IS NOT NULL;

-- ── 2) The coverage the officer is registering this person ONTO ─────────────────────────────────────────────
--
-- An INTENT, not a membership. policy-service owns enrollments and the supervisor's approval is what creates
-- one — that is exactly what registration.coverage_bound has always meant. Writing an enrollment here would
-- grant coverage before anyone approved the application, and would need a cross-service compensation when the
-- application is rejected.
--
-- One row per registration: re-electing a plan before approval is a correction to a pending application, not
-- a second intent to reconcile at approval time.
CREATE TABLE IF NOT EXISTS patient.enrolment_intent (
    registration_id      uuid PRIMARY KEY REFERENCES patient.registration(registration_id) ON DELETE CASCADE,
    plan_id              uuid NOT NULL,
    network_tier_id      uuid NOT NULL,
    -- The member's share of the service price. Bounded here as well as in the domain: a contribution outside
    -- 0..100 silently inverts every cost-share sum that reads it.
    contribution_percent numeric(5,2) NOT NULL CHECK (contribution_percent >= 0 AND contribution_percent <= 100),
    default_branch_id    uuid,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now()
);

-- ── 3) The six standing notes ───────────────────────────────────────────────────────────────────────────────
--
-- Fixed slots rather than free-form rows: slot 1 is ALWAYS the known diagnosis, slot 3 always the insulin
-- flag. A report can then read slot 3 without parsing prose, and the labels stay consistent between the form,
-- the export and the profile.
--
-- visibility is the part that is not cosmetic. Slots 1 and 3 hold clinical facts on a form owned by an
-- administrative role, and 18-security-model.md makes minimum-necessary a matter of code. Classifying them
-- Clinical means the projection that already withholds a scanned lab result from finance withholds these too,
-- while beneficiary management can still FILE them at the desk.
CREATE TABLE IF NOT EXISTS patient.registration_note (
    registration_id uuid NOT NULL REFERENCES patient.registration(registration_id) ON DELETE CASCADE,
    slot            smallint NOT NULL CHECK (slot BETWEEN 1 AND 6),
    value           text NOT NULL,
    visibility      text NOT NULL DEFAULT 'Administrative'
                    CHECK (visibility IN ('Administrative','Clinical')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (registration_id, slot)
);

-- ── 4) Tenant isolation for the two new tables (ADR-0011, mirroring 0003) ───────────────────────────────────
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['enrolment_intent','registration_note']
    LOOP
        EXECUTE format(
            'ALTER TABLE patient.%I ADD COLUMN IF NOT EXISTS tenant_id text NOT NULL DEFAULT ''11111111-1111-1111-1111-111111111111''', t);
        EXECUTE format('ALTER TABLE patient.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE patient.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON patient.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON patient.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;

GRANT SELECT, INSERT, UPDATE, DELETE ON patient.enrolment_intent  TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON patient.registration_note TO hbmp_app;
