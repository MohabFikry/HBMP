-- case-service — Phase 10.1 case management (10-role-matrix §3.11, 23-state-machines). Cases + the ABAC access
-- anchor (case_assignment) + coordination tasks + escalations. Soft-delete + history: benefit/coordination data is
-- never hard-deleted (cases/tasks carry a `deleted` flag; assignments keep their full active/unassigned history).
-- The schema name "case" is a SQL reserved word → always double-quoted.

CREATE SCHEMA IF NOT EXISTS "case";

-- Monotonic per-year case-number sequence backing CASE-YYYY-NNNNNN.
CREATE TABLE IF NOT EXISTS "case".case_seq (
    year       int  PRIMARY KEY,
    last_value int  NOT NULL
);

CREATE TABLE IF NOT EXISTS "case".case_file (
    case_id        uuid PRIMARY KEY,
    case_no        varchar(24) NOT NULL UNIQUE,
    tenant_id      text NOT NULL,
    beneficiary_id uuid NOT NULL,
    category       text NOT NULL CHECK (category IN ('Complex','Chronic','Vulnerable','Escalation')),
    status         text NOT NULL DEFAULT 'Open'
        CHECK (status IN ('Open','Active','OnHold','Resolved','Closed')),
    priority       text NOT NULL DEFAULT 'Normal' CHECK (priority IN ('Low','Normal','High','Urgent')),
    summary        text,
    opened_by      text,
    opened_at      timestamptz NOT NULL DEFAULT now(),
    created_by     text,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    deleted        boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_case_tenant_status ON "case".case_file (tenant_id, status);
CREATE INDEX IF NOT EXISTS ix_case_beneficiary ON "case".case_file (beneficiary_id);

-- The ABAC anchor. An ACTIVE row grants the case manager access; unassigned_at/active=false REVOKES it. Rows are
-- never deleted — assignment history is auditable. At most one ACTIVE assignment per (case, manager).
CREATE TABLE IF NOT EXISTS "case".case_assignment (
    assignment_id   uuid PRIMARY KEY,
    case_id         uuid NOT NULL REFERENCES "case".case_file(case_id),
    case_manager_id uuid NOT NULL,
    assigned_at     timestamptz NOT NULL DEFAULT now(),
    unassigned_at   timestamptz,
    active          boolean NOT NULL DEFAULT true,
    assigned_by     text,
    unassigned_by   text,
    -- an active row must not be unassigned; an unassigned row must carry its timestamp.
    CHECK ((active = true AND unassigned_at IS NULL) OR (active = false AND unassigned_at IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS ix_assignment_mgr_active ON "case".case_assignment (case_manager_id, active);
CREATE INDEX IF NOT EXISTS ix_assignment_case_active ON "case".case_assignment (case_id, active);
CREATE UNIQUE INDEX IF NOT EXISTS ux_assignment_active
    ON "case".case_assignment (case_id, case_manager_id) WHERE active = true;

CREATE TABLE IF NOT EXISTS "case".coordination_task (
    task_id      uuid PRIMARY KEY,
    case_id      uuid NOT NULL REFERENCES "case".case_file(case_id),
    title        text NOT NULL,
    description  text,
    assignee_id  uuid,
    due_at       timestamptz,
    status       text NOT NULL DEFAULT 'Todo' CHECK (status IN ('Todo','InProgress','Done','Cancelled')),
    outcome_note text,
    created_by   text,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    deleted      boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_task_case_status ON "case".coordination_task (case_id, status);

CREATE TABLE IF NOT EXISTS "case".escalation (
    escalation_id   uuid PRIMARY KEY,
    case_id         uuid NOT NULL REFERENCES "case".case_file(case_id),
    raised_by       text,
    raised_to_role  text NOT NULL,
    reason          text NOT NULL,
    status          text NOT NULL DEFAULT 'Raised' CHECK (status IN ('Raised','Acknowledged','Resolved')),
    raised_at       timestamptz NOT NULL DEFAULT now(),
    acknowledged_at timestamptz,
    resolved_at     timestamptz,
    resolution_note text
);
CREATE INDEX IF NOT EXISTS ix_escalation_case_status ON "case".escalation (case_id, status);
CREATE INDEX IF NOT EXISTS ix_escalation_role ON "case".escalation (raised_to_role);

-- Idempotency ledger for mutating endpoints (open case / assign / task create).
CREATE TABLE IF NOT EXISTS "case".processed_request (
    idempotency_key text PRIMARY KEY,
    operation       text NOT NULL,
    entity_id       uuid,
    status_code     int  NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now()
);
