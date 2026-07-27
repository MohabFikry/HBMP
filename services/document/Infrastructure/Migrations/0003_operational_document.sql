-- document-service — 0003 operational documents (phase 19.5b). Additive + idempotent.
--
-- ============================================================================================================
-- A FILE THAT BELONGS TO AN OPERATION, NOT TO A PERSON
-- ============================================================================================================
-- Bulk uploads, their error reports, and data extracts all need somewhere to live. None of them is a
-- beneficiary document: an error report from an enrolment file quotes hundreds of member numbers, so "whose
-- document is this" has no answer the beneficiary model would accept, and a null owner in document.document
-- would be a row every owner-scoped query has to remember to exclude.
--
-- They go through the SAME pipeline — validate, checksum, FAIL-CLOSED ClamAV scan, MinIO — because a second
-- ingest path is a second way for malware to arrive.
--
-- CLASSIFICATION DEFAULTS TO PHI. A bulk error report is the clearest case: "row 4 231: UNHCR number
-- 123-45678 already enrolled" is identifying, clinical-adjacent and bulk. Anything written here is treated as
-- PHI unless the writer positively says otherwise.

CREATE TABLE IF NOT EXISTS document.operational_document (
    document_id      uuid PRIMARY KEY,
    tenant_id        text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',

    kind             varchar(20) NOT NULL CHECK (kind IN ('BulkUpload','BulkErrorReport','Extract')),
    -- The job or run this file belongs to, plus the service that owns that id. A logical reference: there is
    -- no cross-schema FK, and document-service does not know what a bulk job is.
    owner_ref        uuid NOT NULL,
    owner_service    varchar(40) NOT NULL,

    classification   varchar(10) NOT NULL DEFAULT 'PHI' CHECK (classification IN ('PHI','PII','Internal')),
    file_name        varchar(260) NOT NULL,
    content_type     varchar(120) NOT NULL DEFAULT 'text/csv',

    -- Bytes live in MinIO. The database holds the location and the checksum, exactly as document_version does.
    blob_path        text NOT NULL,
    checksum_sha256  varchar(64) NOT NULL,
    size_bytes       bigint NOT NULL,

    is_deleted       boolean NOT NULL DEFAULT false,
    created_at       timestamptz NOT NULL DEFAULT now(),
    created_by       varchar(128)
);
CREATE INDEX IF NOT EXISTS ix_operational_document_owner
    ON document.operational_document (owner_service, owner_ref);
CREATE INDEX IF NOT EXISTS ix_operational_document_kind
    ON document.operational_document (kind, created_at DESC);

GRANT SELECT, INSERT, UPDATE ON document.operational_document TO hbmp_app;

ALTER TABLE document.operational_document ENABLE ROW LEVEL SECURITY;
ALTER TABLE document.operational_document FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_operational_document ON document.operational_document;
CREATE POLICY rls_operational_document ON document.operational_document
    USING (tenant_id = current_setting('app.tenant_id', true));
