-- policy-service — 0014 bulk upload + data extract (phase 19.5b, design 38 §4.4). Additive + idempotent.
--
-- ============================================================================================================
-- ONE ENGINE, TWO DIRECTIONS
-- ============================================================================================================
-- bulk_job / bulk_job_row get data IN; extract_definition / extract_run get it OUT. They share the 19.5 filter
-- vocabulary (Domain/QueryModel.cs) and the payer/branch scope primitives (libs/authz/PayerScope.cs) rather
-- than each growing their own, because three surfaces that disagree about who may see what are three surfaces
-- that each look correct alone.
--
-- The shape of bulk_job is lifted from the phase-12.1 migration toolkit deliberately: batch_id provenance,
-- staging→validate→load→reconcile, rollback-by-batch. That toolkit already answered "how do you load data you
-- can reverse and account for", and a second answer here would be a second thing to get right.

-- ============================================================================================================
-- 1. BULK JOB
-- ============================================================================================================
CREATE TABLE IF NOT EXISTS policy.bulk_job (
    job_id                uuid PRIMARY KEY,
    tenant_id             text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',

    job_type              varchar(32) NOT NULL CHECK (job_type IN
                            ('MemberEnrolment','MemberTermination','PlanChange','GroupAssignment',
                             'ContactUpdate','ProviderTierAssignment','BenefitRuleImport')),
    file_name             varchar(260) NOT NULL,

    -- The uploaded file and the error report both live in document-service (MinIO, behind the ClamAV scan that
    -- cleared them). The error report is PHI-BEARING — row errors quote member numbers and identifiers — so it
    -- is a document with an authorized, audited download, never an inline body and never a log line.
    file_document_id      uuid,
    error_document_id     uuid,

    status                varchar(16) NOT NULL DEFAULT 'Uploaded' CHECK (status IN
                            ('Uploaded','Scanning','Validating','Validated','Committing','Completed',
                             'Failed','RolledBack')),
    failure_code          varchar(64),
    failure_detail        text,

    total_rows            integer NOT NULL DEFAULT 0,
    valid_rows            integer NOT NULL DEFAULT 0,
    invalid_rows          integer NOT NULL DEFAULT 0,
    applied_rows          integer NOT NULL DEFAULT 0,
    failed_rows           integer NOT NULL DEFAULT 0,
    skipped_rows          integer NOT NULL DEFAULT 0,

    -- The reversibility boundary, exactly as MigrationBatch.BatchId is for the 12.1 toolkit: rollback reverses
    -- the rows THIS job applied and nothing that existed before it.
    batch_id              uuid NOT NULL,

    submitted_by_user_id  uuid,
    submitted_by_username varchar(128),
    submitted_at          timestamptz NOT NULL DEFAULT now(),
    completed_at          timestamptz,
    rolled_back_at        timestamptz,
    rolled_back_by        uuid,

    row_version           integer NOT NULL DEFAULT 0,
    created_at            timestamptz NOT NULL DEFAULT now(),
    created_by            uuid,
    updated_at            timestamptz NOT NULL DEFAULT now(),
    updated_by            uuid,

    -- Counts must ADD UP. submitted = valid + invalid is checkable at every status, and a job whose arithmetic
    -- does not close is a job that lost a row — which is the failure mode nobody notices, because the report
    -- still renders.
    CONSTRAINT ck_bulk_job_counts CHECK (total_rows = valid_rows + invalid_rows),
    CONSTRAINT ck_bulk_job_applied CHECK (applied_rows + failed_rows + skipped_rows <= valid_rows)
);
CREATE INDEX IF NOT EXISTS ix_bulk_job_status ON policy.bulk_job (status, submitted_at DESC);
CREATE INDEX IF NOT EXISTS ix_bulk_job_type ON policy.bulk_job (job_type, submitted_at DESC);
CREATE INDEX IF NOT EXISTS ix_bulk_job_batch ON policy.bulk_job (batch_id);
CREATE INDEX IF NOT EXISTS ix_bulk_job_submitter ON policy.bulk_job (submitted_by_user_id, submitted_at DESC);

-- ============================================================================================================
-- 2. BULK JOB ROW — append-only
-- ============================================================================================================
-- raw is what the file said; normalized is what validation made of it. Both are kept: a disputed row is
-- answered with the line the operator uploaded, not with the system's reading of it.
CREATE TABLE IF NOT EXISTS policy.bulk_job_row (
    row_id           uuid PRIMARY KEY,
    tenant_id        text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',
    job_id           uuid NOT NULL REFERENCES policy.bulk_job(job_id) ON DELETE CASCADE,
    row_number       integer NOT NULL,

    raw              jsonb NOT NULL,
    normalized       jsonb,

    status           varchar(10) NOT NULL DEFAULT 'Valid'
                       CHECK (status IN ('Valid','Invalid','Applied','Skipped','Failed')),
    error_code       varchar(64),
    -- BOTH locales. The people who correct these files work in Arabic; an English-only reason means the fix is
    -- guessed from a code, and a guessed fix to an enrolment file is somebody's cover.
    error_detail     text,
    error_detail_ar  text,

    -- The thread from a member record back to the upload that created it, and what rollback-by-batch walks.
    target_ref       uuid,
    -- What the target looked like BEFORE this row changed it. Rollback is a COMPENSATING change back to this,
    -- not a delete: the row being reversed may have updated something that existed long before the job.
    before_snapshot  jsonb,

    applied_at       timestamptz,
    created_at       timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT uq_bulk_job_row UNIQUE (job_id, row_number)
);
CREATE INDEX IF NOT EXISTS ix_bulk_job_row_status ON policy.bulk_job_row (job_id, status);
CREATE INDEX IF NOT EXISTS ix_bulk_job_row_number ON policy.bulk_job_row (job_id, row_number);
CREATE INDEX IF NOT EXISTS ix_bulk_job_row_target ON policy.bulk_job_row (target_ref) WHERE target_ref IS NOT NULL;

-- A row's RAW cells are the record of what was submitted and are never rewritten. status / error / target_ref /
-- applied_at DO change as the row moves through validate → commit → rollback, so the guard is targeted at the
-- immutable columns rather than at the whole row: an append-only table that cannot record the outcome of its
-- own rows would force a second table to hold it.
CREATE OR REPLACE FUNCTION policy.guard_bulk_job_row_immutable()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'bulk_job_row is append-only: delete the job to discard its rows'
            USING ERRCODE = 'raise_exception';
    END IF;
    IF NEW.raw IS DISTINCT FROM OLD.raw
       OR NEW.row_number IS DISTINCT FROM OLD.row_number
       OR NEW.job_id IS DISTINCT FROM OLD.job_id THEN
        RAISE EXCEPTION 'bulk_job_row %: the submitted line is immutable', OLD.row_id
            USING ERRCODE = 'raise_exception';
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_bulk_job_row_immutable ON policy.bulk_job_row;
CREATE TRIGGER trg_bulk_job_row_immutable BEFORE UPDATE OR DELETE ON policy.bulk_job_row
    FOR EACH ROW EXECUTE FUNCTION policy.guard_bulk_job_row_immutable();

-- ============================================================================================================
-- 3. EXTRACT DEFINITION + RUN
-- ============================================================================================================
CREATE TABLE IF NOT EXISTS policy.extract_definition (
    definition_id    uuid PRIMARY KEY,
    tenant_id        text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',

    name             varchar(160) NOT NULL,
    description      text,
    entity           varchar(20) NOT NULL CHECK (entity IN
                       ('Members','Policies','Plans','Coverage','Utilization','NetworkTiers')),
    filter           jsonb NOT NULL DEFAULT '{}'::jsonb,
    columns          jsonb NOT NULL DEFAULT '[]'::jsonb,
    format           varchar(8) NOT NULL DEFAULT 'Csv' CHECK (format IN ('Csv','Xlsx','Json')),

    owner_user_id    uuid,
    is_shared        boolean NOT NULL DEFAULT false,
    schedule_cron    varchar(64),

    -- A SCHEDULED run executes under a service principal with an EXPLICIT payer scope, never the creator's
    -- ambient rights — those change, and are revoked, long after the schedule was set. An empty scope does not
    -- mean unrestricted: it means unconfigured, and the schedule will not run. The difference is a nightly file
    -- containing every payer's membership.
    service_scope_payer_ids text,

    is_deleted       boolean NOT NULL DEFAULT false,
    row_version      integer NOT NULL DEFAULT 0,
    created_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid,
    updated_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid,

    -- A schedule with no service scope is refused at the database too, not only in the application: the
    -- consequence of getting this wrong is an unattended broad disclosure on a timer.
    CONSTRAINT ck_extract_schedule_scoped CHECK (
        schedule_cron IS NULL OR coalesce(btrim(service_scope_payer_ids), '') <> ''
    )
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_extract_definition_name
    ON policy.extract_definition (tenant_id, lower(name)) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS ix_extract_definition_owner ON policy.extract_definition (owner_user_id);
CREATE INDEX IF NOT EXISTS ix_extract_definition_schedule
    ON policy.extract_definition (schedule_cron) WHERE schedule_cron IS NOT NULL AND is_deleted = false;

CREATE TABLE IF NOT EXISTS policy.extract_run (
    run_id                uuid PRIMARY KEY,
    tenant_id             text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',
    definition_id         uuid REFERENCES policy.extract_definition(definition_id),
    entity                varchar(20) NOT NULL CHECK (entity IN
                            ('Members','Policies','Plans','Coverage','Utilization','NetworkTiers')),

    requested_by          uuid,
    requested_by_username varchar(128),
    is_scheduled          boolean NOT NULL DEFAULT false,

    -- WHAT WAS ACTUALLY RUN, not what the definition says today. A definition is editable; a run that points at
    -- a mutable filter cannot answer "what was in the file we sent the donor in March".
    filter_snapshot       jsonb NOT NULL DEFAULT '{}'::jsonb,
    column_snapshot       jsonb NOT NULL DEFAULT '[]'::jsonb,
    -- The columns that were asked for and WITHHELD, with reasons. Kept beside the granted set because "this
    -- file is narrower than the request" is a fact the next reader of the file needs as much as the requester.
    withheld_snapshot     jsonb,

    format                varchar(8) NOT NULL DEFAULT 'Csv' CHECK (format IN ('Csv','Xlsx','Json')),
    as_of                 date,
    row_count             integer NOT NULL DEFAULT 0,
    file_document_id      uuid,
    status                varchar(12) NOT NULL DEFAULT 'Queued'
                            CHECK (status IN ('Queued','Running','Completed','Failed')),
    failure_detail        text,
    started_at            timestamptz NOT NULL DEFAULT now(),
    completed_at          timestamptz
);
CREATE INDEX IF NOT EXISTS ix_extract_run_definition ON policy.extract_run (definition_id, started_at DESC);
CREATE INDEX IF NOT EXISTS ix_extract_run_requester ON policy.extract_run (requested_by, started_at DESC);
CREATE INDEX IF NOT EXISTS ix_extract_run_status ON policy.extract_run (status, started_at DESC);

-- A run is the audit record of a disclosure. It may be updated as it progresses (Queued → Running →
-- Completed) but never deleted: deleting the record of an extract is deleting the record of what left.
CREATE OR REPLACE FUNCTION policy.guard_extract_run_no_delete()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'extract_run % is the record of a disclosure and cannot be deleted', OLD.run_id
        USING ERRCODE = 'raise_exception';
END $$;
DROP TRIGGER IF EXISTS trg_extract_run_no_delete ON policy.extract_run;
CREATE TRIGGER trg_extract_run_no_delete BEFORE DELETE ON policy.extract_run
    FOR EACH ROW EXECUTE FUNCTION policy.guard_extract_run_no_delete();

-- ============================================================================================================
-- 4. Grants + tenant RLS (ADR-0011)
-- ============================================================================================================
GRANT SELECT, INSERT, UPDATE, DELETE ON policy.bulk_job TO hbmp_app;
GRANT SELECT, INSERT, UPDATE ON policy.bulk_job_row TO hbmp_app;
GRANT SELECT, INSERT, UPDATE ON policy.extract_definition TO hbmp_app;
GRANT SELECT, INSERT, UPDATE ON policy.extract_run TO hbmp_app;

ALTER TABLE policy.bulk_job ENABLE ROW LEVEL SECURITY;
ALTER TABLE policy.bulk_job FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_bulk_job ON policy.bulk_job;
CREATE POLICY rls_bulk_job ON policy.bulk_job USING (tenant_id = current_setting('app.tenant_id', true));

ALTER TABLE policy.bulk_job_row ENABLE ROW LEVEL SECURITY;
ALTER TABLE policy.bulk_job_row FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_bulk_job_row ON policy.bulk_job_row;
CREATE POLICY rls_bulk_job_row ON policy.bulk_job_row USING (tenant_id = current_setting('app.tenant_id', true));

ALTER TABLE policy.extract_definition ENABLE ROW LEVEL SECURITY;
ALTER TABLE policy.extract_definition FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_extract_definition ON policy.extract_definition;
CREATE POLICY rls_extract_definition ON policy.extract_definition USING (tenant_id = current_setting('app.tenant_id', true));

ALTER TABLE policy.extract_run ENABLE ROW LEVEL SECURITY;
ALTER TABLE policy.extract_run FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_extract_run ON policy.extract_run;
CREATE POLICY rls_extract_run ON policy.extract_run USING (tenant_id = current_setting('app.tenant_id', true));

-- ============================================================================================================
-- 5. The index rollback-by-batch walks
-- ============================================================================================================
-- Reversing a job means finding every enrolment it created. The idempotency key already carries the job id
-- (BulkIdempotency.KeyFor → 'bulk:{job}:{row}'), so a prefix match resolves it without a second column.
CREATE INDEX IF NOT EXISTS ix_enrollment_idempotency_prefix
    ON policy.enrollment (idempotency_key text_pattern_ops) WHERE idempotency_key IS NOT NULL;
