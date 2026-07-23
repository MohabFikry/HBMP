-- emr-service — Phase 3.1 appointments: recurring availability, materialized bookable slots, appointments
-- with concurrency-safe slot holding, and a pre-booking waitlist. (15-database-erd §7, 23-state-machines §6.)
-- appointment.status uses EXACTLY the canonical persisted set; Requested/Waitlisted live on waitlist_entry.

CREATE SCHEMA IF NOT EXISTS emr;

-- Recurring availability rule: bookable every slot_minutes between start_time..end_time on day_of_week.
CREATE TABLE IF NOT EXISTS emr.provider_availability (
    availability_id uuid PRIMARY KEY,
    provider_id     uuid NOT NULL,
    location_id     uuid NOT NULL,
    doctor_id       uuid,
    day_of_week     int  NOT NULL CHECK (day_of_week BETWEEN 0 AND 6),   -- .NET DayOfWeek (Sun=0)
    start_time      time NOT NULL,
    end_time        time NOT NULL,
    slot_minutes    int  NOT NULL CHECK (slot_minutes > 0),
    CHECK (end_time > start_time)
);
CREATE INDEX IF NOT EXISTS ix_availability_provider ON emr.provider_availability (provider_id, location_id);

-- Concrete bookable slot materialized from availability. Holds at most one active appointment.
CREATE TABLE IF NOT EXISTS emr.appointment_slot (
    slot_id     uuid PRIMARY KEY,
    provider_id uuid NOT NULL,
    location_id uuid NOT NULL,
    doctor_id   uuid,
    slot_start  timestamptz NOT NULL,
    slot_end    timestamptz NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    CHECK (slot_end > slot_start)
);
-- One slot definition per provider+location+doctor+start (idempotent materialization).
CREATE UNIQUE INDEX IF NOT EXISTS ux_slot_defn
    ON emr.appointment_slot (provider_id, location_id, coalesce(doctor_id, '00000000-0000-0000-0000-000000000000'::uuid), slot_start);
CREATE INDEX IF NOT EXISTS ix_slot_lookup ON emr.appointment_slot (provider_id, location_id, slot_start);

CREATE TABLE IF NOT EXISTS emr.appointment (
    appointment_id     uuid PRIMARY KEY,
    beneficiary_id     uuid NOT NULL,                 -- logical FK (patient-service)
    provider_id        uuid NOT NULL,                 -- logical FK (provider-service)
    location_id        uuid NOT NULL,                 -- logical FK (provider-service)
    slot_id            uuid REFERENCES emr.appointment_slot(slot_id),
    appointment_type   text NOT NULL CHECK (appointment_type IN ('WalkIn','Scheduled','Referral','FollowUp')),
    status             text NOT NULL DEFAULT 'Booked'
                       CHECK (status IN ('Booked','CheckedIn','Completed','NoShow','Cancelled')),
    scheduled_start    timestamptz NOT NULL,
    scheduled_end      timestamptz NOT NULL,
    referral_ref       text,                          -- REF-* for Referral bookings
    origin_encounter_id uuid,                         -- originating encounter for FollowUp
    cancel_reason      text,
    no_show            boolean NOT NULL DEFAULT false, -- reporting flag (US-022, set in 3.2)
    idempotency_key    text,
    created_by         text,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    -- Referral must link a REF-*; FollowUp must link an originating encounter (US-020).
    CHECK (appointment_type <> 'Referral' OR referral_ref IS NOT NULL),
    CHECK (appointment_type <> 'FollowUp' OR origin_encounter_id IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS ix_appointment_beneficiary ON emr.appointment (beneficiary_id);
CREATE INDEX IF NOT EXISTS ix_appointment_provider ON emr.appointment (provider_id, location_id);

-- NO DOUBLE-BOOK: at most one ACTIVE (Booked/CheckedIn) appointment may hold a given slot. The losing
-- concurrent INSERT raises 23505 → surfaced as HTTP 409. (Walk-ins are slotless and exempt.)
CREATE UNIQUE INDEX IF NOT EXISTS ux_appointment_active_slot
    ON emr.appointment (slot_id)
    WHERE slot_id IS NOT NULL AND status IN ('Booked','CheckedIn');

-- Idempotent booking: at most one appointment per Idempotency-Key.
CREATE UNIQUE INDEX IF NOT EXISTS ux_appointment_idem
    ON emr.appointment (idempotency_key) WHERE idempotency_key IS NOT NULL;

-- Immutable change history twin (never hard-delete; audit is separate + hash-chained).
CREATE TABLE IF NOT EXISTS emr.appointment_history (
    history_id     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    appointment_id uuid NOT NULL,
    operation      text NOT NULL,
    row_snapshot   jsonb NOT NULL,
    changed_at     timestamptz NOT NULL DEFAULT now()
);
CREATE OR REPLACE FUNCTION emr.write_appointment_history()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO emr.appointment_history (appointment_id, operation, row_snapshot)
    VALUES (NEW.appointment_id, TG_OP, to_jsonb(NEW));
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_appointment_history ON emr.appointment;
CREATE TRIGGER trg_appointment_history AFTER INSERT OR UPDATE ON emr.appointment
    FOR EACH ROW EXECUTE FUNCTION emr.write_appointment_history();

-- Pre-booking waitlist (23 §6 Requested→Waitlisted→Scheduled/Expired). Promotion arrives in 3.2.
CREATE TABLE IF NOT EXISTS emr.waitlist_entry (
    waitlist_id       uuid PRIMARY KEY,
    beneficiary_id    uuid NOT NULL,
    provider_id       uuid NOT NULL,
    location_id       uuid NOT NULL,
    appointment_type  text NOT NULL CHECK (appointment_type IN ('WalkIn','Scheduled','Referral','FollowUp')),
    priority_score    int  NOT NULL DEFAULT 0,
    status            text NOT NULL DEFAULT 'Waitlisted' CHECK (status IN ('Waitlisted','Promoted','Expired')),
    referral_ref      text,
    origin_encounter_id uuid,
    created_by        text,
    created_at        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_waitlist_lookup ON emr.waitlist_entry (provider_id, location_id, status);
