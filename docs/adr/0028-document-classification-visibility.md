# ADR-0028 — Document classification drives visibility; a class may be raised, never lowered (Phase 19.3b)

> Renumbered from ADR-0021 on 2026-07-30 (phase 24 Gate 7). Two unrelated decisions were both
> written as ADR-0021 — this one (phase 19.3b) and the user & access model (phase 21) — so
> "see ADR-0021" resolved to whichever file the reader happened to open. The user & access
> model keeps 0021: it is referenced from the frozen token contract and throughout phase 21.

- Status: Accepted
- Date: 2026-07-28
- Deciders: HBMP platform / benefit administration, privacy
- Context docs: `38-policy-member-administration.md §5.6`, `18-security-model.md`,
  `11-permission-matrix.md`, `20-compliance-checklist.md`, `19-audit-strategy.md`, ADR-0018.

## Context

Policies and members accumulate documents: a signed policy schedule, a payer's approval letter, an identity
document, a member's past medical history. They arrive from different people for different reasons, and they
are emphatically not equally sensitive. A policy schedule is administrative. A past-medical-history upload is
clinical data about a refugee, protected under Egypt's PDPL and UNHCR data-protection alignment.

Who may open which document therefore cannot be a property of the *folder*, and it cannot be a property of the
*uploader's role*. A beneficiary-management officer uploading a scan of a hospital summary does not make that
summary administrative. Deciding visibility by uploader would mean the same document is readable or not
depending on who happened to receive it — which is not a rule, it is an accident.

## Decision

**Every document carries a CLASSIFICATION, and the classification decides who may read it. A class can be
RAISED after upload; it can never be lowered.**

- Classes: `Administrative` / `Financial` / `Identity` / `Clinical` / `Restricted`, matching the note
  visibility vocabulary (ADR-0018) deliberately — one sensitivity language across the two surfaces that most
  often carry free-form content.
- The class is set at upload, defaulted from the document TYPE rather than from the uploader's role, and is
  part of the row rather than of any access-control list.
- The read projection withholds *content* by class: a caller who may not read the class receives existence,
  type, class, uploader and date — never the download link and never the bytes.
- **Raise-only.** `Administrative → Clinical` is allowed and audited. `Clinical → Administrative` is refused
  by the service and by a database CHECK. Reclassifying downward is the one operation that could retroactively
  expose a document to everyone who was previously denied it, with no trace in the document itself that
  anything changed.
- Bytes live in MinIO through document-service's existing validate → checksum → fail-closed ClamAV → store
  pipeline. Policy-service holds only the linkage and the class.
- Download is an **authorized, audited stream**, not a signed URL. A bearer credential in a query string
  outlives every revocation, and a URL redeemed directly at object storage writes no audit event.

## Consequences

- A misclassified-too-high document needs a re-upload to correct, not an edit. That is the cost of the
  asymmetry, and it is the correct side to err on: an over-restricted document is an inconvenience, an
  under-restricted one is a disclosure.
- The default-from-type rule means an operator who uploads without thinking still gets the safe answer. The
  wrong default here is silent, so it is chosen to fail closed.
- Because policy-service stores no bytes, a document's sensitivity is enforced in one place even though two
  services are involved: the class travels with the linkage, and document-service refuses the stream to a
  caller policy-service has not authorized.
- Raising a class is an audited state change. "This became clinical on 3 March and here is who decided that"
  is answerable.

## Alternatives rejected

- **Visibility by uploader role.** Makes the same document readable or not by accident of receipt.
- **Free reclassification in both directions.** A downgrade is a silent bulk disclosure to every previously
  denied reader, and nothing in the document records that it happened.
- **Signed URLs for download.** Cheap and unauditable: the credential outlives revocation and the read never
  reaches the audit log.
