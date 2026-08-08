-- policy-service — 0016 widen the document_class vocabulary.
--
-- Two things happen here.
--
-- 1) THE REGISTRATION CLASSES. Beneficiary management files a copy of the member's card and the paperwork
--    belonging to their case; both were being filed as 'Other', which makes "show me the card copies" a
--    question no query can answer and leaves retention rules with nothing to key on.
--
-- 2) A LATENT BREAK, FIXED. 'IdentityPhoto' was added to the C# DocumentClass enum in phase 20.3 and never to
--    this constraint, so every identification photograph would have been refused by the DATABASE — after the
--    upload had already passed validation, been virus-scanned and been written to MinIO. The consent gate and
--    the narrow photo allow-list around it were all reachable; the insert at the end was not.
--
-- Rebuilt rather than extended because a CHECK constraint cannot be added to. Named this time, so the next
-- migration that needs to widen it does not have to discover an auto-generated name.

DO $$
DECLARE existing text;
BEGIN
    -- The 0010 constraint was declared inline and therefore auto-named. Discover it rather than assume
    -- 'policy_document_document_class_check', which is only what Postgres HAPPENS to generate.
    SELECT con.conname INTO existing
    FROM pg_constraint con
    JOIN pg_class rel ON rel.oid = con.conrelid
    JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
    WHERE nsp.nspname = 'policy'
      AND rel.relname = 'policy_document'
      AND con.contype = 'c'
      AND pg_get_constraintdef(con.oid) LIKE '%document_class%'
      AND con.conname <> 'ck_pdoc_document_class';

    IF existing IS NOT NULL THEN
        -- migrate-compat: contract-ok (the CHECK is WIDENED, never narrowed — a CHECK cannot be added to,
        -- so the only way to permit more values is to drop and re-add. Old code keeps working; this is an
        -- expand.)
        EXECUTE format('ALTER TABLE policy.policy_document DROP CONSTRAINT %I', existing);
    END IF;
END $$;

ALTER TABLE policy.policy_document DROP CONSTRAINT IF EXISTS ck_pdoc_document_class;  -- migrate-compat: contract-ok (dropped only to be re-added WIDER on the next statement)

ALTER TABLE policy.policy_document
    ADD CONSTRAINT ck_pdoc_document_class CHECK (document_class IN (
        -- Policy scope
        'PolicyContract','BenefitSchedule','PayerAgreement','Endorsement','FinancialGuarantee',
        'PolicyCorrespondence',
        -- Member scope
        'IdentityDocument','ProofOfEligibility','EnrolmentForm','ConsentForm',
        'PastMedicalHistory','MedicalReport','LabResult','Prescription','DischargeSummary',
        'Referral','InvoiceReceipt','MemberCorrespondence','Other',
        -- Phase 20.3 — the identification photograph. Present in the enum since 20.3; missing here until now.
        'IdentityPhoto',
        -- Registration (this phase): a scan of the physical card, and the case file's paperwork.
        'CardCopy','CaseDocument'));
