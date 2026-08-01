-- patient-service — 0005 who filed a registration, and the conversation about it.
--
-- Two gaps this closes, both of which the approval worklist made visible the moment it grew a date column.
--
-- 1) NOBODY WAS RECORDED AS HAVING FILED THE APPLICATION. registration carried created_at and no actor, so
--    "who registered this person?" could only be answered from the audit trail — and the audit trail is not a
--    queryable operational field, it is evidence. It also made the request-info decision undeliverable: the
--    supervisor asks for more information and the system had no idea whose queue that belonged in.
--
-- 2) NOTES WERE A SINGLE COLUMN, SO EVERY DECISION OVERWROTE THE LAST ONE. registration.notes is set by the
--    decision endpoint, which means "UNHCR letter is expired" is gone the instant anyone decides again, and
--    the officer had nowhere to answer. A request for information that cannot be replied to is a dead end
--    dressed as a workflow.
--
-- Additive and idempotent per the expand/contract rule: registration.notes stays and keeps its meaning (the
-- CURRENT outstanding note, which is what the worklist column shows), while the thread below is the history
-- and the reply channel. An older build reading only registration.notes keeps working unchanged.

-- ── 1) Who filed it ─────────────────────────────────────────────────────────────────────────────────────────
--
-- The subject is the durable identity; the display name is a COPY taken at write time, deliberately. Resolving
-- a name through identity-service on every worklist read would make the queue depend on that service being up
-- to render a column, and a staff member who leaves must still be named on the applications they filed — the
-- record is of what happened, and it does not change when the directory does.
ALTER TABLE patient.registration ADD COLUMN IF NOT EXISTS created_by      text;
ALTER TABLE patient.registration ADD COLUMN IF NOT EXISTS created_by_name text;

-- The worklist's request-info fan-out looks up "applications filed by me" and the officer's own inbox link
-- resolves back through it.
CREATE INDEX IF NOT EXISTS ix_registration_created_by ON patient.registration (created_by)
    WHERE created_by IS NOT NULL;

-- ── 2) The conversation ─────────────────────────────────────────────────────────────────────────────────────
--
-- Append-only by construction: no UPDATE or DELETE grant is issued below, so an entry cannot be edited away
-- after the fact. That is the same rule the audit trail follows and for the same reason — a supervisor's
-- stated reason for refusing an application is evidence, and evidence that can be quietly rewritten is not.
--
-- kind distinguishes the two things on the thread, because they answer to different rules: a Decision entry is
-- written by the decision endpoint and carries which decision it was, while a Reply is what the officer (or
-- the supervisor) adds afterwards. Rendering them identically would let a reply be mistaken for a ruling.
CREATE TABLE IF NOT EXISTS patient.registration_thread (
    entry_id        uuid PRIMARY KEY,
    registration_id uuid NOT NULL REFERENCES patient.registration(registration_id) ON DELETE CASCADE,
    tenant_id       text NOT NULL,
    kind            text NOT NULL CHECK (kind IN ('Decision','Reply')),
    -- Approve / RequestInfo / Reject, and NULL on a reply. Stored rather than parsed back out of the body,
    -- so the thread can be filtered by outcome without reading prose.
    decision        text CHECK (decision IS NULL OR decision IN ('Approve','RequestInfo','Reject')),
    body            text NOT NULL,
    author_user_id  text,
    author_name     text,
    author_role     text,
    created_at      timestamptz NOT NULL DEFAULT now()
);

-- The thread is always read whole, oldest first, for one registration.
CREATE INDEX IF NOT EXISTS ix_registration_thread_reg
    ON patient.registration_thread (registration_id, created_at);

-- ── 3) Tenant isolation (ADR-0011, mirroring 0003/0004) ─────────────────────────────────────────────────────
ALTER TABLE patient.registration_thread ENABLE ROW LEVEL SECURITY;
ALTER TABLE patient.registration_thread FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_registration_thread ON patient.registration_thread;
CREATE POLICY rls_registration_thread ON patient.registration_thread
    USING (tenant_id = current_setting('app.tenant_id', true));

-- SELECT and INSERT only. See the append-only note above: withholding UPDATE/DELETE from the application role
-- is what enforces it, rather than a comment asking callers not to.
GRANT SELECT, INSERT ON patient.registration_thread TO hbmp_app;
