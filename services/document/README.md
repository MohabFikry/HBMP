# document-service

Phase 1.3 (US-002). Document metadata + validated, malware-scanned uploads attached to a beneficiary. Blob BYTES live in object storage (MinIO/S3); only metadata + checksum in Postgres (15-database-erd §12).

## Delivered
- Entities (Document/DocumentVersion) + `0001_document_schema.sql`.
- `UploadValidator`: allowed MIME (pdf/jpeg/png) + max size BEFORE storing; clear reason on reject.
- `DocumentUploadService`: validate → SHA-256 checksum → **malware scan (fail-closed)** → store clean blob → create/version with timestamp + uploader. Only clean files are attached.
- `ClamAvScanner` (INSTREAM over TCP to clamd), `MinioBlobStore` (private bucket).
- API: `POST /api/v1/beneficiaries/{id}/documents` (multipart; 201 stored / 400 rejected / **422 malware-quarantined**), `GET` list (metadata only). Every path audited; `DocumentAttached` event via outbox.

## Tests (12, green offline)
Validation matrix (allowed/disallowed MIME, oversize, empty); clean→Stored (version+checksum+uploader, blob written once); malware-positive→Quarantined (never stored, fail-closed); disallowed type rejected before scan/store; second upload versions the document.
