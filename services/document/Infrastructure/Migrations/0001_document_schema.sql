-- document-service — 0001 metadata schema (15-database-erd §12). Blobs live in object storage.
CREATE SCHEMA IF NOT EXISTS document;

CREATE TABLE IF NOT EXISTS document.document (
    document_id         uuid PRIMARY KEY,
    doc_type            text NOT NULL CHECK (doc_type IN ('IDScan','Consent','Referral','LabResult','ImagingReport')),
    owner_beneficiary_id uuid NOT NULL,
    classification      text NOT NULL CHECK (classification IN ('PHI','PII','Internal')),
    blob_container      text NOT NULL,
    current_version_no  int NOT NULL DEFAULT 0,
    is_deleted          boolean NOT NULL DEFAULT false,
    created_at          timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_document_owner ON document.document (owner_beneficiary_id);

CREATE TABLE IF NOT EXISTS document.document_version (
    document_version_id uuid PRIMARY KEY,
    document_id         uuid NOT NULL REFERENCES document.document(document_id),
    version_no          int NOT NULL,
    blob_path           text NOT NULL,
    checksum_sha256     text NOT NULL,
    size_bytes          bigint NOT NULL,
    uploaded_at         timestamptz NOT NULL DEFAULT now(),
    uploaded_by         text,
    UNIQUE (document_id, version_no)
);
