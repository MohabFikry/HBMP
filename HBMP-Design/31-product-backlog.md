# 31 — Product Backlog

> Back to [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [28-mvp-definition.md](28-mvp-definition.md) · [30-technical-backlog.md](30-technical-backlog.md) · [32-user-stories.md](32-user-stories.md) · [33-sprint-roadmap.md](33-sprint-roadmap.md)

Product backlog of **epics → features**, organized by module/portal and mapped to the 7 patient-journey phases. Priority uses MoSCoW (M=Must, S=Should, C=Could, W=Won't-now). "MVP" marks items in the v1 walking skeleton ([28-mvp-definition.md](28-mvp-definition.md)). Feature detail lands as stories in [32-user-stories.md](32-user-stories.md).

Legend — Priority: M/S/C/W · MVP: ✅ in / ➖ out.

---

## EPIC-01 — Beneficiary Registration & Policy Admin (Phase 1)

| ID | Feature | Priority | MVP | Depends on |
|----|---------|----------|-----|-----------|
| FEAT-0101 | Multi-identifier registration wizard (National ID/Passport/Refugee ID/UNHCR/Member No) | M | ✅ | TECH identity, DB |
| FEAT-0102 | Document upload + validation + malware scan | M | ✅ | document-service |
| FEAT-0103 | Eligibility & coverage/policy assignment | M | ✅ | policy-service |
| FEAT-0104 | Registration approval workflow (Pending→Active) | M | ✅ | approvals core |
| FEAT-0105 | Member number issuance + record activation | M | ✅ | — |
| FEAT-0106 | Family/dependents management | S | ✅ | — |
| FEAT-0107 | Status lifecycle mgmt (suspend/expire/block/reactivate) | M | ✅ | — |
| FEAT-0108 | Duplicate detection / record merge | S | ➖ | search |
| FEAT-0109 | QR beneficiary card | C | ➖ | — |

## EPIC-02 — Eligibility Check (Phase 2)

| ID | Feature | Priority | MVP | Depends on |
|----|---------|----------|-----|-----------|
| FEAT-0201 | Reception search by ID/Passport/Card/Policy/Phone | M | ✅ | search, eligibility |
| FEAT-0202 | Minimum-necessary eligibility result card | M | ✅ | permission engine |
| FEAT-0203 | Coverage + remaining limits display | M | ✅ | policy-service |
| FEAT-0204 | Visit-history summary | S | ✅ | emr summary API |
| FEAT-0205 | Real-time status (Active/Expired/Suspended/Blocked/Pending) | M | ✅ | — |
| FEAT-0206 | QR/scan lookup | C | ➖ | — |

## EPIC-03 — Appointment Management (Phase 3)

| ID | Feature | Priority | MVP | Depends on |
|----|---------|----------|-----|-----------|
| FEAT-0301 | Walk-in + queue management | M | ✅ | — |
| FEAT-0302 | Scheduled appointments + doctor availability | M | ✅ | provider-service |
| FEAT-0303 | Reschedule / cancel | M | ✅ | — |
| FEAT-0304 | No-show handling + backfill | S | ✅ | — |
| FEAT-0305 | Follow-up & referral-driven appointments | S | ✅ | orders |
| FEAT-0306 | Waiting list | S | ➖ | — |
| FEAT-0307 | Multi-clinic scheduling + calendar views | S | ➖ | — |
| FEAT-0308 | SMS/WhatsApp reminders | C | ➖ | notification ext |

## EPIC-04 — Clinical Consultation / EMR (Phase 4)

| ID | Feature | Priority | MVP | Depends on |
|----|---------|----------|-----|-----------|
| FEAT-0401 | Patient clinical summary (treating-only) | M | ✅ | ABAC treating rel. |
| FEAT-0402 | SOAP note + diagnosis (ICD-10) | M | ✅ | master data |
| FEAT-0403 | Vitals, allergies, medication history | M | ✅ | — |
| FEAT-0404 | Create investigation/radiology orders | M | ✅ | orders-service |
| FEAT-0405 | Create e-prescriptions | M | ✅ | pharmacy-service |
| FEAT-0406 | Referrals | S | ✅ | — |
| FEAT-0407 | Medical certificates | C | ➖ | — |
| FEAT-0408 | Drug interaction & allergy alerts | S | ➖ | PBM rules |
| FEAT-0409 | Follow-up scheduling from encounter | S | ✅ | appointments |

## EPIC-05 — Laboratory & Imaging (Phase 5)

| ID | Feature | Priority | MVP | Depends on |
|----|---------|----------|-----|-----------|
| FEAT-0501 | Provider order queue + search | M | ✅ | orders-service |
| FEAT-0502 | Atomic order-line consume (no reuse, duplicate-proof) | M | ✅ | concurrency guard |
| FEAT-0503 | Upload result + attach report | M | ✅ | document-service |
| FEAT-0504 | Partial fulfillment (remaining lines stay active) | M | ✅ | — |
| FEAT-0505 | Mark completed; result routed to ordering doctor/approvals | M | ✅ | — |
| FEAT-0506 | QR order lookup | C | ➖ | — |

## EPIC-06 — Pharmacy (Phase 6)

| ID | Feature | Priority | MVP | Depends on |
|----|---------|----------|-----|-----------|
| FEAT-0601 | Prescription search (Rx/Patient/Policy/Passport/Member) | M | ✅ | pharmacy-service |
| FEAT-0602 | Dispense + partial dispensing (remaining stays available) | M | ✅ | — |
| FEAT-0603 | Batch number + expiry tracking | M | ✅ | — |
| FEAT-0604 | Reject if expired/completed | M | ✅ | — |
| FEAT-0605 | Substitution with approved alternatives | S | ➖ | formulary |
| FEAT-0606 | Out-of-stock workflow | S | ➖ | — |
| FEAT-0607 | Formulary / PBM rules / generic substitution | C | ➖ | PBM |

## EPIC-07 — Medical Approval (Phase 7)

| ID | Feature | Priority | MVP | Depends on |
|----|---------|----------|-----|-----------|
| FEAT-0701 | Approval worklist + review (EMR/notes/docs) | M | ✅ | approvals-service |
| FEAT-0702 | Approve / reject / request-info / partial | M | ✅ | — |
| FEAT-0703 | Mandatory rationale + rejection reason | M | ✅ | — |
| FEAT-0704 | Emergency approval / override (break-glass audited) | S | ✅ | audit |
| FEAT-0705 | Manual authorization (search member, create without provider) | S | ✅ | — |
| FEAT-0706 | Approval SLA/TAT board | S | ➖ | reporting |

## EPIC-08 — Provider Network (cross-cutting)

| ID | Feature | Priority | MVP | Depends on |
|----|---------|----------|-----|-----------|
| FEAT-0801 | Provider directory + onboarding | M | ✅ | provider-service |
| FEAT-0802 | Contracts, coverage, locations, provider users | M | ✅ | — |
| FEAT-0803 | Provider isolation enforcement | M | ✅ | security |
| FEAT-0804 | Provider performance metrics | S | ➖ | reporting |

## EPIC-09 — Notifications

| ID | Feature | Priority | MVP | Depends on |
|----|---------|----------|-----|-----------|
| FEAT-0901 | In-app notifications + provider/approval alerts | M | ✅ | notification-service |
| FEAT-0902 | Email notifications | S | ✅ | — |
| FEAT-0903 | Escalations engine | S | ➖ | — |
| FEAT-0904 | SMS / WhatsApp | C | ➖ | ext gateway |

## EPIC-10 — Reporting & Dashboards

| ID | Feature | Priority | MVP | Depends on |
|----|---------|----------|-----|-----------|
| FEAT-1001 | Operational KPIs + clinic workload | S | ✅ | reporting-service |
| FEAT-1002 | Approval TAT, rejected requests | S | ➖ | — |
| FEAT-1003 | Provider/drug/lab/radiology utilization | S | ➖ | — |
| FEAT-1004 | Top diagnoses/medications, demographics | C | ➖ | — |
| FEAT-1005 | Financial summaries | S | ➖ | finance |
| FEAT-1006 | Executive dashboard | S | ➖ | — |

## EPIC-11 — Identity, Roles & Admin

| ID | Feature | Priority | MVP | Depends on |
|----|---------|----------|-----|-----------|
| FEAT-1101 | SSO + MFA login (Keycloak) | M | ✅ | identity |
| FEAT-1102 | Role & permission management (RBAC+ABAC) | M | ✅ | policy engine |
| FEAT-1103 | Master data mgmt (ICD/CPT/Drug/ATC) | M | ✅ | — |
| FEAT-1104 | Audit & access-review console | M | ✅ | audit-service |
| FEAT-1105 | System config / notification templates | S | ✅ | — |
| FEAT-1106 | Tenant management | C | ➖ | — |

## EPIC-12 — Future / Roadmap (Won't-now for v1)

Telemedicine · AI Clinical Decision Support · Arabic NLP · Patient & Provider mobile apps · Offline clinics · FHIR interoperability · HL7 integration · Insurance integration · UNHCR/government integration · Digital referral network · Inventory · Billing · full PBM. (All `W` / ➖ — architected-for, not built in v1; see [16-service-architecture.md](16-service-architecture.md) extensibility.)

> **Moved out of EPIC-12:** **Claims Management** (with its OCR document ingest for reimbursement receipts) is **no longer deferred** — it is in scope as **Phase 10b**, built on the completed core (authorizations, fulfillment, contracts/tariffs). See EPIC-13 below and [36-claims-management.md](36-claims-management.md).

## EPIC-13 — Claims Management (Phase 10b, post-v1)

Design: [36-claims-management.md](36-claims-management.md) · Service: `claims-service` ([16](16-service-architecture.md) §2) · Roles/SoD: [11-permission-matrix.md](11-permission-matrix.md). All items are **post-v1** (➖) and sequenced after R4; priorities are MoSCoW *within Phase 10b*.

| ID | Feature | Priority | MVP | Depends on |
|----|---------|----------|-----|-----------|
| FEAT-1301 | Auto-derived claim generation from fulfillment/dispense events, priced from contract tariff | M | ➖ | orders, pharmacy, provider contracts |
| FEAT-1302 | Provider claim submission + document upload and matching to fulfillment records | M | ➖ | FEAT-1301, document-service |
| FEAT-1303 | Beneficiary reimbursement request (receipts + result evidence) with OCR pre-fill and confidence scoring | M | ➖ | document-service, `IDocumentOcrProvider` |
| FEAT-1304 | Reimbursement manual-assessment queue for low-confidence / unmatched extractions | M | ➖ | FEAT-1303 |
| FEAT-1305 | Batch creation by date range, provider branch, provider group, or manual selection (one open batch per claim) | M | ➖ | FEAT-1301, provider-service |
| FEAT-1306 | Automated pre-adjudication engine with all-reasons coded output + system recommendation | M | ➖ | eligibility, approvals, tariffs, rules engine |
| FEAT-1307 | Line-level Claims Officer decisions (approve/partial/deny) with batch rollup totals | M | ➖ | FEAT-1305, FEAT-1306 |
| FEAT-1308 | Mandatory coded denial reasons + rationale on deny/adjust/override | M | ➖ | FEAT-1307 |
| FEAT-1309 | Request-info and clinical-review routing (medical necessity → Medical Approval/Director) | M | ➖ | approvals-service |
| FEAT-1310 | Reconciliation worklist (billed-not-delivered, delivered-not-billed, price/quantity variance, duplicate) | M | ➖ | FEAT-1301, FEAT-1302 |
| FEAT-1311 | Adjustments — price/quantity correction, deduction, recovery/clawback, write-off, reversal/void, reallocation (append-only, signed, coded) | M | ➖ | FEAT-1307 |
| FEAT-1312 | Settlement advice generation (immutable, WORM-stored) + CSV/XLSX/PDF exports, no clinical fields | M | ➖ | FEAT-1307, document-service |
| FEAT-1313 | Claim/batch appeals + re-adjudication | S | ➖ | FEAT-1307 |
| FEAT-1314 | Segregation-of-duties + dual control above a value threshold | M | ➖ | identity, FEAT-1307 |
| FEAT-1315 | Claims KPIs & dashboards (TAT, approval/denial rate, top denial reasons, adjustment value, OCR auto-match rate, aged unbilled) | S | ➖ | reporting-service |
| FEAT-1316 | Provider claims portal view (own claims/batches/statements only) | S | ➖ | FEAT-1302, provider isolation |
| FEAT-1317 | Configurable auto-approval of clean low-value lines (off by default) | C | ➖ | FEAT-1306 |

---

### Prioritization summary
- **Must + MVP** items constitute the v1 walking skeleton and Releases R0–R4 ([29-delivery-plan.md](29-delivery-plan.md)).
- Reporting, substitutions, escalations and SMS/WhatsApp are fast-follows (R5).
- **EPIC-13 (Claims Management)** is post-v1 but **committed** as Phase 10b — it depends on authorizations, fulfillment and contract tariffs all being live, so it cannot start earlier ([36-claims-management.md](36-claims-management.md)).
- EPIC-12 is deliberately deferred but the service-oriented core keeps each addable without re-platforming.

### Cross-references
- MVP scope: [28-mvp-definition.md](28-mvp-definition.md) · Stories & AC: [32-user-stories.md](32-user-stories.md)
- Enablers: [30-technical-backlog.md](30-technical-backlog.md) · Sprint mapping: [33-sprint-roadmap.md](33-sprint-roadmap.md)
