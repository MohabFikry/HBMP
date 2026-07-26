-- claims-service — Phase 10b.6 beneficiary reimbursement + OCR (36-claims-management §3.3, 22-data-dictionary
-- §10A.6/§10A.8, 23-state-machines §10).
--
-- A beneficiary (or Reception/Case Manager on their behalf) submits receipts + result/dispense evidence against an
-- AUTHORIZED underlying order/prescription. OCR is ASSISTIVE, NEVER AUTHORITATIVE: every ocr_extraction is append-only
-- (no row is ever overwritten) and no extracted value affects money until a human sets accepted_by/accepted_at. A
-- reimbursement is capped at min(contract tariff, receipt) and ALWAYS requires an explicit Claims Officer decision —
-- there is no auto-approval path at any confidence. NO bank/payout details are stored here (payout runs through Finance).

CREATE TABLE IF NOT EXISTS claims.reimbursement_request (
    request_id             uuid PRIMARY KEY,
    claim_id               uuid REFERENCES claims.claim(claim_id),
    beneficiary_id         uuid NOT NULL,
    submitted_by           text NOT NULL,
    acting_for             text,
    submitted_at           timestamptz NOT NULL DEFAULT now(),
    receipt_total          numeric(14,2) NOT NULL CHECK (receipt_total >= 0),
    currency_code          char(3) NOT NULL DEFAULT 'EGP',
    status                 varchar(20) NOT NULL DEFAULT 'Submitted'
        CHECK (status IN ('Submitted','OcrProcessing','AutoMatched','ManualAssessment','Adjudicating',
                          'Approved','PartiallyApproved','Denied','Paid','Void')),
    match_confidence       numeric(5,4) CHECK (match_confidence IS NULL OR (match_confidence >= 0 AND match_confidence <= 1)),
    match_method           varchar(12) NOT NULL DEFAULT 'Unmatched' CHECK (match_method IN ('AutoOcr','Manual','Unmatched')),
    linked_order_id        uuid,
    linked_prescription_id uuid,
    tenant_id              text NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_reimbursement_beneficiary ON claims.reimbursement_request (beneficiary_id, submitted_at DESC);
CREATE INDEX IF NOT EXISTS ix_reimbursement_status ON claims.reimbursement_request (status);
CREATE INDEX IF NOT EXISTS ix_reimbursement_claim ON claims.reimbursement_request (claim_id) WHERE claim_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS claims.ocr_extraction (
    extraction_id   uuid PRIMARY KEY,
    request_id      uuid NOT NULL REFERENCES claims.reimbursement_request(request_id),
    document_id     uuid NOT NULL,
    field_name      varchar(40) NOT NULL CHECK (field_name IN ('provider','service_date','amount','currency','code')),
    extracted_value varchar(256),
    confidence      numeric(5,4) NOT NULL CHECK (confidence >= 0 AND confidence <= 1),
    page            integer CHECK (page IS NULL OR page >= 1),
    region          jsonb,
    engine          text NOT NULL,
    engine_version  text NOT NULL,
    extracted_at    timestamptz NOT NULL DEFAULT now(),
    accepted_by     text,
    accepted_at     timestamptz
);
CREATE INDEX IF NOT EXISTS ix_ocr_document_field ON claims.ocr_extraction (document_id, field_name);
CREATE INDEX IF NOT EXISTS ix_ocr_unaccepted ON claims.ocr_extraction (confidence) WHERE accepted_by IS NULL;

-- APPEND-ONLY: an extraction is never overwritten (a re-run is new rows). A human CONFIRM sets accepted_by/accepted_at,
-- which is the ONLY permitted UPDATE (guarded by the trigger); DELETE is never permitted.
CREATE OR REPLACE FUNCTION claims.reject_ocr_mutation() RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'append-only: DELETE on ocr_extraction is not permitted' USING ERRCODE = 'raise_exception';
    END IF;
    -- UPDATE is allowed ONLY to record human acceptance; the extracted value/confidence/region are immutable.
    IF NEW.extracted_value IS DISTINCT FROM OLD.extracted_value
       OR NEW.confidence   IS DISTINCT FROM OLD.confidence
       OR NEW.region       IS DISTINCT FROM OLD.region
       OR NEW.field_name   IS DISTINCT FROM OLD.field_name
       OR NEW.document_id  IS DISTINCT FROM OLD.document_id THEN
        RAISE EXCEPTION 'append-only: an OCR extraction value is immutable' USING ERRCODE = 'raise_exception';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_ocr_append_only ON claims.ocr_extraction;
CREATE TRIGGER trg_ocr_append_only
    BEFORE UPDATE OR DELETE ON claims.ocr_extraction
    FOR EACH ROW EXECUTE FUNCTION claims.reject_ocr_mutation();

-- ---------------------------------------------------------------------------------------------------------------
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE ON claims.reimbursement_request TO hbmp_app;
        -- ocr_extraction: INSERT + SELECT + the acceptance UPDATE (value columns are trigger-guarded); no DELETE.
        GRANT SELECT, INSERT, UPDATE ON claims.ocr_extraction TO hbmp_app;
    END IF;
END $$;

-- RLS: tenant separation. Reimbursement requests are Mersal-staff / member facing (no provider column); the tenant
-- GUC is the scope. ocr_extraction inherits its request's visibility.
ALTER TABLE claims.reimbursement_request ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.reimbursement_request FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_reimbursement_request ON claims.reimbursement_request;
CREATE POLICY rls_reimbursement_request ON claims.reimbursement_request USING (
    tenant_id = current_setting('app.tenant_id', true)
);

ALTER TABLE claims.ocr_extraction ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.ocr_extraction FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_ocr_extraction ON claims.ocr_extraction;
CREATE POLICY rls_ocr_extraction ON claims.ocr_extraction USING (
    EXISTS (SELECT 1 FROM claims.reimbursement_request r WHERE r.request_id = ocr_extraction.request_id)
);
