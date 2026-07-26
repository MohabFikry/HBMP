-- claims-service — Phase 10b.5 provider-submitted claims + document matching (36-claims-management §3.2/§5 checks
-- 4/5, 22-data-dictionary §10A.6/§10A.7, 18-security-model provider isolation).
--
-- A provider (or Mersal on their behalf) submits an invoice; each asserted line is MATCHED to a delivered/authorized
-- fulfillment on (provider, beneficiary, code, service date ± tolerance, authorization). A match creates a priced,
-- fulfillment-anchored payable line on a ProviderSubmitted claim — and the 10b.1 no-double-billing index guarantees a
-- re-submission of an already-claimed fulfillment cannot create a second payable line (DUPLICATE_CLAIM). An unmatched
-- line becomes a NO_FULFILLMENT_RECORD / RequiresManualReview line in the manual-assessment queue — never auto-approved.
-- claim_document stores only a REFERENCE to the bytes held (scanned + encrypted) in document-service.

CREATE TABLE IF NOT EXISTS claims.claim_submission (
    submission_id          uuid PRIMARY KEY,
    claim_id               uuid REFERENCES claims.claim(claim_id),
    provider_id            uuid NOT NULL,
    beneficiary_id         uuid NOT NULL,
    invoice_number         varchar(60),
    currency_code          char(3) NOT NULL DEFAULT 'EGP',
    status                 varchar(20) NOT NULL DEFAULT 'Received'
        CHECK (status IN ('Received','Matched','PartiallyMatched','Unmatched')),
    tenant_id              text NOT NULL,
    submitted_by           text NOT NULL,
    submitted_on_behalf_of text,
    submitted_at           timestamptz NOT NULL DEFAULT now(),
    idempotency_key        text NOT NULL
);
-- One submission per idempotency key: a retried POST returns the first submission unchanged.
CREATE UNIQUE INDEX IF NOT EXISTS ux_submission_idempotency ON claims.claim_submission (idempotency_key);
CREATE INDEX IF NOT EXISTS ix_submission_provider ON claims.claim_submission (provider_id, submitted_at DESC);
CREATE INDEX IF NOT EXISTS ix_submission_claim ON claims.claim_submission (claim_id) WHERE claim_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS claims.claim_submission_line (
    submission_line_id  uuid PRIMARY KEY,
    submission_id       uuid NOT NULL REFERENCES claims.claim_submission(submission_id),
    code_system         varchar(10) NOT NULL CHECK (code_system IN ('CPT','LOINC','LOCAL','DRUG')),
    code                varchar(20) NOT NULL,
    description         varchar(200),
    service_date        date NOT NULL,
    quantity            numeric(14,3) NOT NULL CHECK (quantity > 0),
    billed_amount       numeric(14,2) NOT NULL CHECK (billed_amount >= 0),
    authorization_id    uuid,
    outcome             varchar(12) NOT NULL CHECK (outcome IN ('Matched','Unmatched','Duplicate')),
    claim_line_id       uuid REFERENCES claims.claim_line(claim_line_id),
    price_variance      boolean NOT NULL DEFAULT false,
    reason_code         varchar(40)
);
CREATE INDEX IF NOT EXISTS ix_submission_line_submission ON claims.claim_submission_line (submission_id);

CREATE TABLE IF NOT EXISTS claims.claim_document (
    claim_document_id uuid PRIMARY KEY,
    claim_id          uuid REFERENCES claims.claim(claim_id),
    request_id        uuid,
    document_id       uuid NOT NULL,
    doc_type          varchar(20) NOT NULL
        CHECK (doc_type IN ('Invoice','Receipt','ResultProof','DispenseProof','Statement','SettlementAdvice','Other')),
    linked_by         text NOT NULL,
    linked_at         timestamptz NOT NULL DEFAULT now(),
    -- exactly one of claim / request is set (a document belongs to a claim OR a reimbursement request, not both).
    CHECK ((claim_id IS NULL) <> (request_id IS NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_claim_document_claim ON claims.claim_document (claim_id, document_id)
    WHERE claim_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_claim_document_request ON claims.claim_document (request_id, document_id)
    WHERE request_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_claim_document_document ON claims.claim_document (document_id);

-- ---------------------------------------------------------------------------------------------------------------
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE ON claims.claim_submission, claims.claim_submission_line TO hbmp_app;
        GRANT SELECT, INSERT ON claims.claim_document TO hbmp_app;
    END IF;
END $$;

-- RLS: provider isolation (a provider sees only its OWN submissions) + tenant separation. Same GUC pattern as claim.
ALTER TABLE claims.claim_submission ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.claim_submission FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_claim_submission ON claims.claim_submission;
CREATE POLICY rls_claim_submission ON claims.claim_submission USING (
    tenant_id = current_setting('app.tenant_id', true)
    AND (
        coalesce(current_setting('app.provider_id', true), '') = ''
        OR provider_id::text = current_setting('app.provider_id', true)
    )
);

-- child rows inherit their submission's visibility.
ALTER TABLE claims.claim_submission_line ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.claim_submission_line FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_claim_submission_line ON claims.claim_submission_line;
CREATE POLICY rls_claim_submission_line ON claims.claim_submission_line USING (
    EXISTS (SELECT 1 FROM claims.claim_submission s WHERE s.submission_id = claim_submission_line.submission_id)
);

ALTER TABLE claims.claim_document ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.claim_document FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_claim_document ON claims.claim_document;
CREATE POLICY rls_claim_document ON claims.claim_document USING (
    claim_id IS NULL
    OR EXISTS (SELECT 1 FROM claims.claim c WHERE c.claim_id = claim_document.claim_id)
);
