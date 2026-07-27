-- policy-service — 0010 documents on policy and member (phase 19.3b, design 38 §5b). Additive + idempotent.
--
-- REUSE, NOT REBUILD. The bytes live in document-service/MinIO, which already owns MIME/size validation, the
-- fail-closed ClamAV scan, checksum_sha256, storage and versioning. This table adds the POLICY/MEMBER LINKAGE
-- and the CLASSIFICATION on top. There is deliberately no blob column, no second scanner and no second store —
-- a parallel upload pipeline would be a second place for malware to get in and a second place for retention to
-- be forgotten.
--
-- ---------------------------------------------------------------------------------------------------------
-- THE POINT OF THE FEATURE IS THE CLASSIFICATION, so it is worth being precise about the two date fields and
-- the two "how sensitive" fields, because each pair looks redundant and is not:
--
--   document_date vs uploaded_at   The date ON the document versus the date it reached us. Past medical
--                                  history is ordered CLINICALLY, by when the care happened — a discharge
--                                  summary from 2019 scanned in today belongs in 2019 on the member's
--                                  timeline, not at the top. Sorting by upload order would make a member's
--                                  history read backwards.
--
--   document_class vs             What KIND of document it is versus WHO may read it. A LabResult is clinical
--   visibility_class              because of what it is; a PolicyContract is administrative for the same
--                                  reason. But sensitivity can be HIGHER than the class implies, which is
--                                  what sensitive_category carries — see below.
--
-- sensitive_category resolves a gap in the build prompt. It says "anything mental-health, HIV/STI, genetic,
-- substance-use, reproductive or GBV-related → Restricted", but none of those is a document_class: they are
-- properties of the CONTENT of a MedicalReport or a LabResult. So the uploader declares the category from the
-- design-37 §5 vocabulary, and that RAISES the default to Restricted. Without it, the rule would have been
-- unimplementable and every such document would have defaulted to merely Clinical.
-- ---------------------------------------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS policy.policy_document (
    link_id            uuid PRIMARY KEY,
    tenant_id          text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',

    scope              varchar(10) NOT NULL CHECK (scope IN ('Policy','Member')),
    scope_ref          uuid NOT NULL,                      -- policy_id | enrollment_id

    document_id        uuid NOT NULL,                      -- document-service ref (a VALUE, no cross-schema FK)
    version_no         int NOT NULL DEFAULT 1 CHECK (version_no > 0),
    supersedes_link_id uuid REFERENCES policy.policy_document(link_id),

    document_class     varchar(24) NOT NULL CHECK (document_class IN (
                         -- Policy scope
                         'PolicyContract','BenefitSchedule','PayerAgreement','Endorsement','FinancialGuarantee',
                         'PolicyCorrespondence',
                         -- Member scope
                         'IdentityDocument','ProofOfEligibility','EnrolmentForm','ConsentForm',
                         'PastMedicalHistory','MedicalReport','LabResult','Prescription','DischargeSummary',
                         'Referral','InvoiceReceipt','MemberCorrespondence','Other')),

    visibility_class   varchar(20) NOT NULL CHECK (visibility_class IN
                         ('Administrative','Financial','Clinical','Restricted')),

    -- design 37 §5 categories. Present ⇒ the default visibility is Restricted, whatever the class implies.
    sensitive_category varchar(20) CHECK (sensitive_category IN
                         ('MentalHealth','HivSti','Genetic','SubstanceUse','Reproductive','Gbv')),

    title              varchar(200) NOT NULL,
    description        text,
    document_date      date,                               -- the date ON the document (see header)
    issuing_provider   varchar(200),

    -- The signature, snapshotted — same discipline as notes (19.3): it must survive the uploader being
    -- renamed or de-provisioned, which a join would not.
    uploaded_by_user_id  uuid NOT NULL,
    uploaded_by_username varchar(128) NOT NULL,
    uploaded_by_display  varchar(200) NOT NULL,
    uploaded_at          timestamptz NOT NULL,

    status             varchar(12) NOT NULL DEFAULT 'Active'
                         CHECK (status IN ('Active','Superseded','Withdrawn')),
    withdrawn_by_user_id  uuid,
    withdrawn_by_username varchar(128),
    withdrawn_at          timestamptz,
    withdrawal_reason     text,

    expires_on         date,                               -- ID cards, consents
    verified_by_user_id  uuid,
    verified_by_username varchar(128),
    verified_at          timestamptz,
    verification_note    text,

    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),

    -- Withdrawing without who/when/why leaves a document marked wrong with no account of why — on a surface
    -- whose whole purpose is evidencing a decision.
    CONSTRAINT ck_pdoc_withdrawal_complete CHECK (
        status <> 'Withdrawn'
        OR (withdrawn_by_user_id IS NOT NULL AND withdrawn_at IS NOT NULL
            AND withdrawal_reason IS NOT NULL AND length(btrim(withdrawal_reason)) > 0)
    ),
    -- Verification is all-or-nothing: a verified_at with no verifier is unattributable.
    CONSTRAINT ck_pdoc_verification_complete CHECK (
        (verified_by_user_id IS NULL) = (verified_at IS NULL)
    )
);
CREATE INDEX IF NOT EXISTS ix_pdoc_scope ON policy.policy_document (scope, scope_ref, uploaded_at DESC);
-- Past medical history is read in CLINICAL order, so document_date leads this index, not uploaded_at.
CREATE INDEX IF NOT EXISTS ix_pdoc_clinical_order
    ON policy.policy_document (scope, scope_ref, document_date DESC NULLS LAST);
CREATE INDEX IF NOT EXISTS ix_pdoc_class ON policy.policy_document (document_class);
CREATE INDEX IF NOT EXISTS ix_pdoc_status ON policy.policy_document (status);
CREATE INDEX IF NOT EXISTS ix_pdoc_expiry ON policy.policy_document (expires_on) WHERE expires_on IS NOT NULL;

-- ---- Classification + immutability, enforced by the database ------------------------------------------------
-- Visibility may be RAISED but NEVER lowered — the same rule notes carry (0009), for the same reason: lowering
-- it retroactively exposes clinical material to roles that were correctly denied it, and nothing about the row
-- looks different afterwards. Enforced here so no path can do it, not only the upload endpoint.
CREATE OR REPLACE FUNCTION policy.guard_policy_document_immutable()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'documents are never deleted: withdraw link % instead (the bytes and the record stay)', OLD.link_id
            USING ERRCODE = 'raise_exception';
    END IF;

    IF OLD.document_id IS DISTINCT FROM NEW.document_id
       OR OLD.version_no IS DISTINCT FROM NEW.version_no
       OR OLD.scope IS DISTINCT FROM NEW.scope
       OR OLD.scope_ref IS DISTINCT FROM NEW.scope_ref THEN
        RAISE EXCEPTION 'document link % is immutable: re-upload to create a new version', OLD.link_id
            USING ERRCODE = 'raise_exception';
    END IF;

    IF OLD.uploaded_by_user_id IS DISTINCT FROM NEW.uploaded_by_user_id
       OR OLD.uploaded_by_username IS DISTINCT FROM NEW.uploaded_by_username
       OR OLD.uploaded_at IS DISTINCT FROM NEW.uploaded_at THEN
        RAISE EXCEPTION 'document link % is signed: its uploader and timestamp can never be changed', OLD.link_id
            USING ERRCODE = 'raise_exception';
    END IF;

    IF OLD.visibility_class IS DISTINCT FROM NEW.visibility_class
       AND policy.note_visibility_rank(NEW.visibility_class) < policy.note_visibility_rank(OLD.visibility_class) THEN
        RAISE EXCEPTION 'document % visibility can be raised but never lowered (% → %)',
            OLD.link_id, OLD.visibility_class, NEW.visibility_class
            USING ERRCODE = 'raise_exception';
    END IF;

    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_policy_document_immutable ON policy.policy_document;
CREATE TRIGGER trg_policy_document_immutable BEFORE UPDATE OR DELETE ON policy.policy_document
    FOR EACH ROW EXECUTE FUNCTION policy.guard_policy_document_immutable();

-- ---- Grants + tenant RLS (ADR-0011) -------------------------------------------------------------------------
-- No DELETE grant: a withdrawn document keeps its row AND its bytes. "Wrong member" is a reason to mark it,
-- not to make the mistake unfindable.
GRANT SELECT, INSERT, UPDATE ON policy.policy_document TO hbmp_app;

ALTER TABLE policy.policy_document ENABLE ROW LEVEL SECURITY;
ALTER TABLE policy.policy_document FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_policy_document ON policy.policy_document;
CREATE POLICY rls_policy_document ON policy.policy_document USING (
    tenant_id = current_setting('app.tenant_id', true)
);
