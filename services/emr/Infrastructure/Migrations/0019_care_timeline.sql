-- ============================================================================================================
-- 0019 — the care episode timeline (ADR-0031).
-- ============================================================================================================
-- `GET /appointments/{id}/timeline` reads emr.appointment_history, which a row trigger fills on every write
-- to the appointment ROW. That makes it excellent at "booked, rescheduled, checked in" and structurally
-- incapable of anything else: nothing that happens to the patient after check-in touches that row.
--
-- So a desk asking "why is this member still here at four o'clock?" got a timeline that stopped at arrival,
-- and the work an appointment actually causes — the visit, the note, the orders, the prescriptions, the
-- dispensing — was recorded in five services and joined up in none.
--
-- This table is the episode. It is keyed on the ENCOUNTER, because a visit is the unit a clinician acts in
-- and everything downstream descends from one; `appointment_id` is carried alongside so the episode can be
-- read from either end, and is null for a walk-in, whose episode is no less whole for never having been
-- booked.
--
-- WHAT A STEP IS NOT: clinical content. A step is a label, a time, an actor and a reference — "OrderPlaced,
-- 09:22, Dr Karim, ORD-2026-000014" — because this timeline is read by reception and the call centre as well
-- as by clinicians, and a step naming the medicine would put a prescription in front of a desk that is
-- structurally forbidden it. What a reference resolves to stays behind the owning service's own gate.

CREATE TABLE IF NOT EXISTS emr.care_timeline (
    step_id        uuid PRIMARY KEY,
    tenant_id      text NOT NULL,
    -- The episode. One of these is always present; both usually are.
    encounter_id   uuid,
    appointment_id uuid,
    beneficiary_id uuid NOT NULL,
    -- The step's own name, from the catalogue in ADR-0031. Deliberately text and not an enum: the set grows
    -- as services join the episode, and a CHECK constraint here would mean a migration in emr every time
    -- another service learned to say something — which is the coupling this design exists to avoid.
    step           text NOT NULL,
    occurred_at    timestamptz NOT NULL,
    -- Who did it (subject id) and which service said so. `source` is what lets a reader tell a step emr
    -- wrote itself from one that arrived by event, which matters when one is missing.
    actor          text,
    source         text NOT NULL DEFAULT 'emr',
    -- The business key of the thing this step is about: ENC-*, ORD-*, RX-*, AUTH-*. Never an internal id and
    -- never a description.
    reference      text,
    -- The event that produced the step, for the consumer's dedupe. Null for steps emr writes directly.
    event_id       uuid
);

-- The read: one episode, oldest first. Both directions are used — the workspace opens from the encounter,
-- the desk's board from the appointment.
CREATE INDEX IF NOT EXISTS ix_care_timeline_encounter
    ON emr.care_timeline (encounter_id, occurred_at) WHERE encounter_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_care_timeline_appointment
    ON emr.care_timeline (appointment_id, occurred_at) WHERE appointment_id IS NOT NULL;

-- Delivery is at-least-once (outbox → broker), so the same event can arrive twice. Dedupe belongs in the
-- database rather than in the consumer's memory: a consumer that restarts has forgotten what it processed,
-- and this is the only place that has not.
CREATE UNIQUE INDEX IF NOT EXISTS ux_care_timeline_event
    ON emr.care_timeline (event_id) WHERE event_id IS NOT NULL;

-- A step is APPENDED and never rewritten: an episode's history is what happened. A cancelled order adds an
-- OrderCancelled step beside its OrderPlaced — it does not remove one.
COMMENT ON TABLE emr.care_timeline IS
    'Append-only episode-of-care steps (ADR-0031). Keyed on the encounter, parented by the appointment. '
    'Labels, times, actors and business-key references only — never clinical content, because reception and '
    'the call centre read this too.';

-- RLS, in exactly the shape 0007 gave every other emr table: same policy name (`rls_<table>`), same USING
-- clause, FORCE so the owning role is bound by it too. A second isolation pattern in one schema is a second
-- thing to audit, and the one that differs is the one nobody checks.
--
-- Privileges come from 0007's ALTER DEFAULT PRIVILEGES, which already grants hbmp_app SELECT/INSERT/UPDATE/
-- DELETE on tables created in this schema afterwards — so no GRANT is needed here, and writing one anyway
-- would suggest the default does not apply.
ALTER TABLE emr.care_timeline ENABLE ROW LEVEL SECURITY;
ALTER TABLE emr.care_timeline FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_care_timeline ON emr.care_timeline;
CREATE POLICY rls_care_timeline ON emr.care_timeline
    USING (tenant_id = current_setting('app.tenant_id', true));

-- A row belonging to no tenant is invisible to every tenant and deletable by none — 0015's rule, applied to
-- the new table rather than discovered on it later.
ALTER TABLE emr.care_timeline DROP CONSTRAINT IF EXISTS ck_care_timeline_tenant_not_blank;
ALTER TABLE emr.care_timeline ADD CONSTRAINT ck_care_timeline_tenant_not_blank
    CHECK (length(btrim(tenant_id)) > 0);
