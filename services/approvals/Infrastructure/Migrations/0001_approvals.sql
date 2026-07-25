-- approvals-service — Phase 7 medical approvals (22-data-dictionary §9, 23-state-machines §5). The authorization
-- aggregate + the APPEND-ONLY decision ledger. Status/priority/source enums are CHECK-constrained to the canonical
-- sets exactly. authorization_decision is insert-only: a trigger + a revoked UPDATE/DELETE grant make corrections
-- new rows, never edits (19-audit-strategy: immutable decision record).

CREATE SCHEMA IF NOT EXISTS approvals;

-- Monotonic per-year authorization-number sequence backing AUTH-YYYY-NNNNNN.
CREATE TABLE IF NOT EXISTS approvals.auth_seq (
    year       int  PRIMARY KEY,
    last_value int  NOT NULL
);

CREATE TABLE IF NOT EXISTS approvals.authorization (
    authorization_id       uuid PRIMARY KEY,
    auth_no                varchar(24) NOT NULL UNIQUE,
    beneficiary_id         uuid NOT NULL,
    source                 text NOT NULL CHECK (source IN ('OrderLine','Prescription','Manual')),
    source_ref             text,
    requesting_provider_id uuid,
    service_codes          jsonb NOT NULL DEFAULT '[]'::jsonb,
    requested_scope        jsonb NOT NULL DEFAULT '{}'::jsonb,
    priority               text NOT NULL DEFAULT 'Routine' CHECK (priority IN ('Routine','Urgent','Emergency')),
    status                 text NOT NULL DEFAULT 'Submitted'
        CHECK (status IN ('Draft','Submitted','UnderReview','Approved','PartiallyApproved','Rejected',
                          'InfoRequested','Overridden','EmergencyApproved','Expired')),
    assigned_reviewer_id   uuid,
    sla_due_at             timestamptz,
    submitted_at           timestamptz NOT NULL DEFAULT now(),
    decided_at             timestamptz,
    tat_seconds            int,
    sla_breached           boolean NOT NULL DEFAULT false,
    idempotency_key        text,
    created_by             text,
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_at             timestamptz NOT NULL DEFAULT now(),
    -- manual authorizations have no requesting provider; all others must name one.
    CHECK (source = 'Manual' OR requesting_provider_id IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS ix_auth_status_sla ON approvals.authorization (status, sla_due_at);
CREATE INDEX IF NOT EXISTS ix_auth_beneficiary ON approvals.authorization (beneficiary_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_auth_idempotency ON approvals.authorization (idempotency_key)
    WHERE idempotency_key IS NOT NULL;

-- APPEND-ONLY decision ledger. break_glass rows (emergency/override/manual) carry a mandatory justification.
CREATE TABLE IF NOT EXISTS approvals.authorization_decision (
    decision_id      uuid PRIMARY KEY,
    authorization_id uuid NOT NULL REFERENCES approvals.authorization(authorization_id),
    decision         text NOT NULL CHECK (decision IN ('Approved','PartiallyApproved','Rejected',
                                                       'InfoRequested','Overridden','EmergencyApproved')),
    reviewer_id      uuid NOT NULL,
    decided_at       timestamptz NOT NULL DEFAULT now(),
    rationale        text,
    approved_scope   jsonb,
    break_glass      boolean NOT NULL DEFAULT false,
    justification    text,
    correlation_id   text,
    -- a break-glass decision must carry a justification (defense in depth for the handler check).
    CHECK (break_glass = false OR justification IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS ix_auth_decision_auth ON approvals.authorization_decision (authorization_id);

-- Immutability: the decision ledger is insert-only. A trigger blocks UPDATE/DELETE even for a mis-granted role.
CREATE OR REPLACE FUNCTION approvals.deny_decision_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'approvals.authorization_decision is append-only: % is denied', TG_OP;
END $$;

DROP TRIGGER IF EXISTS trg_auth_decision_no_mutate ON approvals.authorization_decision;
CREATE TRIGGER trg_auth_decision_no_mutate BEFORE UPDATE OR DELETE ON approvals.authorization_decision
    FOR EACH ROW EXECUTE FUNCTION approvals.deny_decision_mutation();

-- And revoke UPDATE/DELETE from the app role (the platform connects as hbmp_app, NOBYPASSRLS non-superuser).
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        REVOKE UPDATE, DELETE ON approvals.authorization_decision FROM hbmp_app;
    END IF;
END $$;

-- Idempotency ledger for mutating endpoints (assign / decide / break-glass / manual).
CREATE TABLE IF NOT EXISTS approvals.processed_request (
    idempotency_key text PRIMARY KEY,
    operation       text NOT NULL,
    authorization_id uuid,
    status_code     int  NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now()
);
