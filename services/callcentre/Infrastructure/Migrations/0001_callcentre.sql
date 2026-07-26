-- callcentre-service — Phase 15.1 contact-centre foundation (design 37 §3 MemberScoped, 10-role-matrix Call Center,
-- 19-audit-strategy, 20-compliance-checklist). Two aggregates: the call itself (call_interaction) and the
-- caller-verification attempts bound to it. History is append-only; nothing is hard-deleted.
--
-- THE PRIVACY INVARIANT (enforced here by column design): caller_verification stores only WHICH identifier TYPES
-- were confirmed (verified_identifiers jsonb array of type names) — NEVER the identifier VALUES the caller recited.
-- Those values live in patient-service and must not be duplicated into the call log.

CREATE SCHEMA IF NOT EXISTS callcentre;

-- Monotonic per-year sequence backing CALL-YYYY-NNNNNN.
CREATE TABLE IF NOT EXISTS callcentre.call_seq (
    year       int  PRIMARY KEY,
    last_value int  NOT NULL
);

CREATE TABLE IF NOT EXISTS callcentre.call_interaction (
    interaction_id  uuid PRIMARY KEY,
    call_ref        varchar(20) NOT NULL UNIQUE,
    tenant_id       text NOT NULL,
    beneficiary_id  uuid,                                    -- null until identified + verified
    agent_user_id   uuid NOT NULL,
    direction       text NOT NULL CHECK (direction IN ('Inbound','Outbound')),
    started_at      timestamptz NOT NULL DEFAULT now(),
    ended_at        timestamptz,
    reason_code     text CHECK (reason_code IN ('BookAppointment','RescheduleAppointment','CancelAppointment',
                        'AppointmentEnquiry','EligibilityEnquiry','UpdateContact','Complaint','Other')),
    outcome         text CHECK (outcome IN ('Resolved','FollowUpRequired','Transferred','Abandoned','NoAction')),
    notes           text,
    status          text NOT NULL DEFAULT 'Open' CHECK (status IN ('Open','Closed')),
    created_by      text,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_ci_agent ON callcentre.call_interaction (tenant_id, agent_user_id, started_at);
CREATE INDEX IF NOT EXISTS ix_ci_beneficiary ON callcentre.call_interaction (beneficiary_id);

-- Verification attempts. verified_identifiers holds only the TYPE names confirmed (e.g. ["MemberNo","DateOfBirth"]).
-- A Failed attempt is persisted AND audited — never silently discarded.
CREATE TABLE IF NOT EXISTS callcentre.caller_verification (
    verification_id      uuid PRIMARY KEY,
    interaction_id       uuid NOT NULL REFERENCES callcentre.call_interaction(interaction_id),
    beneficiary_id       uuid NOT NULL,
    tenant_id            text NOT NULL,
    verified_identifiers jsonb NOT NULL DEFAULT '[]'::jsonb,  -- TYPES only, never values
    result               text NOT NULL CHECK (result IN ('Passed','Failed')),
    failure_reason       varchar(64),
    verified_at          timestamptz NOT NULL DEFAULT now(),
    verified_by          text
);
CREATE INDEX IF NOT EXISTS ix_cv_interaction ON callcentre.caller_verification (interaction_id);
CREATE INDEX IF NOT EXISTS ix_cv_beneficiary ON callcentre.caller_verification (beneficiary_id, verified_at DESC);

-- Idempotency ledger for mutating endpoints (open interaction / verification / appointment actions).
CREATE TABLE IF NOT EXISTS callcentre.processed_request (
    idempotency_key text PRIMARY KEY,
    operation       text NOT NULL,
    entity_id       uuid,
    status_code     int  NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now()
);
