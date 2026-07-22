# 07 — Functional Requirements

> Back to [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [08-non-functional-requirements.md](08-non-functional-requirements.md) · [09-information-architecture.md](09-information-architecture.md) · [10-role-matrix.md](10-role-matrix.md) · [11-permission-matrix.md](11-permission-matrix.md) · [23-state-machines.md](23-state-machines.md)

This document specifies the **functional requirements (FRs)** for the Mersal HBMP. Requirements are grouped by module, each carrying:

- **ID** — `FR-<MOD>-nnn` (stable, never reused).
- **Statement** — the observable capability, written as "The system shall…".
- **Priority** — MoSCoW: **M** = Must, **S** = Should, **C** = Could, **W** = Won't (this release).
- **Rationale** — why it exists.
- **Phase** — which of the 7 care phases it primarily serves: `Registration`, `Eligibility`, `Appointments`, `Consultation`, `Lab & Imaging`, `Pharmacy`, `Approval` (or `Cross-cutting`).

**Module codes:** `REG` Registration/Policy · `ELG` Eligibility · `APT` Appointments · `CLIN` Clinical/EMR · `LAB` Lab & Imaging · `RX` Pharmacy · `AUTH` Approvals · `NET` Provider Network · `NOT` Notifications · `RPT` Reporting · `IAM` Admin/Identity · `MDM` Master Data · `AUD` Audit.

> **Traceability:** every FR is expected to trace forward to user stories in [32-user-stories.md](32-user-stories.md) and back to processes in [05-business-process-maps.md](05-business-process-maps.md) / [06-bpmn-diagrams.md](06-bpmn-diagrams.md).

---

## 1. Registration & Policy (`REG`)

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-REG-001 | The system shall register a new beneficiary capturing demographics, contact, preferred language (AR/EN), and consent, generating one immutable `beneficiary_id` (UUID v7). | M | Canonical subject of record; single surrogate prevents duplicate identities. | Registration |
| FR-REG-002 | The system shall accept **0..n identifiers** per beneficiary from {National ID, Passport, Refugee ID, UNHCR Number, Org Member Number}, each with type, value, issuing authority, and verification status. | M | Refugees rarely hold a single ID type; identity must be flexible. | Registration |
| FR-REG-003 | The system shall run **duplicate detection** on registration (fuzzy name + DOB + identifier match) and warn the operator before creating a potential duplicate. | M | Prevents fragmented records and double-spend of benefits. | Registration |
| FR-REG-004 | The system shall support **merge** of two beneficiary records with full audit and reversible history, preserving both identifier sets. | S | Duplicates will occur; merge must be safe and traceable. | Registration |
| FR-REG-005 | The system shall issue a human-readable **Member Number** `MRS-M-<10 digits>` (checksummed) upon policy assignment. | M | Front-desk and provider lookups need a speakable key. | Registration |
| FR-REG-006 | The system shall allow attaching a **Policy** to a member defining coverage categories, monetary/visit limits, co-pay rules, and a validity window (start/end). | M | Eligibility derives from Policy; benefits must be bounded. | Registration |
| FR-REG-007 | The system shall model **beneficiary status** lifecycle `Pending → Active → (Suspended \| Expired \| Blocked \| Inactive)` per [0A §6](0A-DESIGN-FOUNDATIONS.md). | M | Downstream eligibility depends on status. | Registration |
| FR-REG-008 | The system shall support **household/family grouping** so dependents can be linked to a head-of-household while retaining independent records. | S | Refugee caseloads are family-centric; simplifies enrollment. | Registration |
| FR-REG-009 | The system shall capture and store **consent artifacts** (data processing, treatment) with version, timestamp, and channel, and block clinical use where mandatory consent is absent. | M | GDPR/HIPAA lawful basis; see [20-compliance-checklist.md](20-compliance-checklist.md). | Registration |
| FR-REG-010 | The system shall support **document upload** at registration (ID scans, referral letters) with virus scan on ingest and typed metadata. | M | Verification evidence must be retained securely. | Registration |
| FR-REG-011 | The system shall allow **re-activation / renewal** of an expired or suspended policy with a new validity window and reason code. | M | Coverage cycles renew; must not require re-registration. | Registration |
| FR-REG-012 | The system shall enforce **field-level data minimization** so Reception-role registration screens never expose clinical/EMR fields. | M | Hard privacy requirement; see [11-permission-matrix.md](11-permission-matrix.md). | Cross-cutting |
| FR-REG-013 | The system shall record **verification status** transitions (Unverified → Verified → Rejected) per identifier with verifier identity and evidence link. | S | Establishes trust level for eligibility decisions. | Registration |
| FR-REG-014 | The system shall support **bulk import** of beneficiaries (e.g., UNHCR roster) via validated file upload with per-row error reporting and staging before commit. | C | Onboarding cohorts efficiently. | Registration |

---

## 2. Eligibility (`ELG`)

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-ELG-001 | The system shall answer, in real time, **"can this beneficiary receive service X now?"** returning `Eligible \| Not Eligible \| Partial \| Needs Approval` with reason codes. | M | The core benefit decision gates every visit. | Eligibility |
| FR-ELG-002 | The eligibility decision shall be **derived from** {policy validity window + beneficiary status + coverage category + remaining limits + required authorizations}. | M | Deterministic, auditable eligibility. | Eligibility |
| FR-ELG-003 | The system shall present an **eligibility result card** exposing only minimum-necessary fields to Reception (identity match, status, coverage summary) and **no diagnoses or clinical data**. | M | Data minimization; Reception must not see EMR. | Eligibility |
| FR-ELG-004 | The system shall compute and display **remaining limits** (monetary and/or visit counts) per coverage category. | M | Prevents over-utilization and surprise denials. | Eligibility |
| FR-ELG-005 | The system shall **snapshot** the eligibility decision (inputs + result + timestamp) and attach it to the resulting encounter. | M | Auditability and dispute resolution. | Eligibility |
| FR-ELG-006 | The system shall flag when a requested service **requires prior approval** and route the user to initiate an authorization. | M | Controlled/high-cost services must be gated. | Eligibility / Approval |
| FR-ELG-007 | The system shall support **manual eligibility override** by an authorized role (e.g., Case Manager/Medical Director) with mandatory reason and audit. | S | Edge cases and humanitarian exceptions occur. | Eligibility |
| FR-ELG-008 | The system shall expose eligibility as a **reusable service/API** callable by Reception, Call Center, and provider portals without duplicating logic. | M | Reusable core "spine"; single source of truth. | Cross-cutting |
| FR-ELG-009 | The system shall degrade gracefully to a **cached last-known eligibility** (read-only, flagged as stale) if the live service is briefly unavailable. | S | Continuity of care during partial outages. | Eligibility |

---

## 3. Appointments (`APT`)

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-APT-001 | The system shall allow booking an appointment against a **provider + service + slot**, validating eligibility at booking time. | M | Prevents booking ineligible or uncovered services. | Appointments |
| FR-APT-002 | The system shall support **walk-in / same-day** visit creation without a pre-booked slot (Reception queue). | M | Refugee clinics are heavily walk-in. | Appointments |
| FR-APT-003 | The system shall support **reschedule** of an appointment preserving history and notifying the beneficiary. | M | Plans change; continuity of the record. | Appointments |
| FR-APT-004 | The system shall support **cancellation** with reason codes and free the slot for reuse. | M | Capacity efficiency. | Appointments |
| FR-APT-005 | The system shall mark **no-show** after a configurable grace period and optionally trigger follow-up. | S | Utilization tracking; re-engagement. | Appointments |
| FR-APT-006 | The system shall manage **provider schedules / availability templates** (working hours, slot length, capacity, blackout dates). | M | Slots cannot be booked without availability. | Appointments |
| FR-APT-007 | The system shall provide a **Reception queue / day-list** view showing checked-in, waiting, in-consultation, and completed states. | M | Front-desk flow control. | Appointments |
| FR-APT-008 | The system shall generate an **Encounter** `ENC-<yyyymmdd>-<seq>` on check-in, linking eligibility snapshot, provider, and beneficiary. | M | Encounter is the clinical/benefit activity anchor. | Appointments |
| FR-APT-009 | The system shall support **Call Center booking/rescheduling** on behalf of a beneficiary, with the agent seeing only scheduling-relevant, non-clinical data. | M | Data minimization for Call Center role. | Appointments |
| FR-APT-010 | The system shall support **referral-driven appointments** where an accepted referral (`REF-…`) pre-populates the target provider/service. | S | Continuity across the network. | Appointments |
| FR-APT-011 | The system shall prevent **double-booking** of the same slot via atomic slot reservation. | M | Data integrity of the schedule. | Appointments |

---

## 4. Clinical & EMR (`CLIN`)

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-CLIN-001 | The system shall let a treating clinician record a **SOAP note** (Subjective, Objective, Assessment, Plan) bound to an encounter. | M | Structured clinical documentation. | Consultation |
| FR-CLIN-002 | The system shall record **vital signs** (BP, HR, temp, SpO₂, weight, height, BMI-derived) with units and timestamps. | M | Core clinical data; nurse & doctor capture. | Consultation |
| FR-CLIN-003 | The system shall record **diagnoses coded with ICD-10** (ICD-11 ready), supporting primary/secondary and problem-list status. | M | Coded morbidity; interoperability & reporting. | Consultation |
| FR-CLIN-004 | The system shall maintain a longitudinal **problem list, medication list, and allergy list** per beneficiary. | M | Safe, continuous care. | Consultation |
| FR-CLIN-005 | The system shall enforce **"doctors see only patients they treat"** — clinicians access a beneficiary's EMR only via an active care relationship (encounter/referral). | M | Need-to-know; provider isolation. | Cross-cutting |
| FR-CLIN-006 | The system shall let clinicians create **investigation Orders** (lab/imaging) and **Prescriptions** directly from the consultation. | M | Orders/Rx originate in the encounter. | Consultation |
| FR-CLIN-007 | The system shall surface **allergy and drug-interaction alerts** (against Allergy DB + interaction DB) at prescribing time. | M | Patient safety (PBM). | Consultation / Pharmacy |
| FR-CLIN-008 | The system shall support **nurse workflows**: triage, vitals capture, procedure/administration notes, scoped to nursing permissions. | M | Nurses are distinct role/portal. | Consultation |
| FR-CLIN-009 | The system shall allow **structured + free-text** clinical notes, retaining full version history (append-only, no hard delete). | M | Medico-legal integrity; see [19-audit-strategy.md](19-audit-strategy.md). | Consultation |
| FR-CLIN-010 | The system shall let clinicians view returned **lab/imaging results** in-context once uploaded and released. | M | Closes the diagnostic loop. | Lab & Imaging / Consultation |
| FR-CLIN-011 | The system shall support **referrals** (`REF-…`) to another provider/specialty with reason and clinical summary, honoring minimum-necessary sharing. | S | Care coordination across network. | Consultation |
| FR-CLIN-012 | The system shall present the clinician a **timeline/summary** of the beneficiary's prior encounters, meds, and results they are authorized to see. | S | Contextual, safe decision-making. | Consultation |
| FR-CLIN-013 | The system shall let the approval team view **EMR, clinical notes, and reports** required to adjudicate an authorization. | M | Approval role is explicitly permitted clinical visibility. | Approval |

---

## 5. Lab & Imaging (`LAB`)

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-LAB-001 | The system shall place an **investigation Order** with one or more order lines, each an orderable test/procedure (LOINC-ready for labs; CPT-coded). | M | Structured, codeable orders. | Lab & Imaging |
| FR-LAB-002 | The system shall route each order to the appropriate **provider queue** (Lab / Imaging Center) via domain event when it becomes `Active`. | M | Event-driven availability to providers. | Lab & Imaging |
| FR-LAB-003 | The system shall let a provider **consume an order line** — atomically claiming it so it cannot be reused (see invariants §13). | M | Prevents double-spend of benefits. | Lab & Imaging |
| FR-LAB-004 | The system shall let the provider **upload a result/report** (structured values and/or document) against a consumed order line, with release control. | M | Returns the diagnostic result. | Lab & Imaging |
| FR-LAB-005 | The system shall enforce that **Labs cannot see prescriptions** and **Imaging cannot see unrelated clinical data** beyond the order's minimum-necessary context. | M | Hard data-minimization rule. | Cross-cutting |
| FR-LAB-006 | The system shall track order-line lifecycle `Requested → PendingApproval → (Approved\|Rejected) → Active → PartiallyUsed → Completed` (+ `Expired`,`Cancelled`). | M | Canonical order state machine. | Lab & Imaging |
| FR-LAB-007 | The system shall support **partial fulfillment** where some order lines are consumed/resulted while others remain `Active` (order = `PartiallyUsed`). | M | Multi-test orders complete incrementally. | Lab & Imaging |
| FR-LAB-008 | The system shall validate that a result upload references a **valid, consumed, not-yet-completed** order line owned by the uploading provider. | M | Integrity + provider isolation. | Lab & Imaging |
| FR-LAB-009 | The system shall notify the ordering clinician when a **result is released**. | S | Closes the loop promptly. | Lab & Imaging |
| FR-LAB-010 | The system shall flag **critical/abnormal results** (against configured reference ranges) for expedited clinician attention. | S | Patient safety. | Lab & Imaging |
| FR-LAB-011 | The system shall reject any attempt to consume an **already-consumed** order line and return an idempotent, explanatory error. | M | Enforces the consumption invariant. | Cross-cutting |

---

## 6. Pharmacy (`RX`)

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-RX-001 | The system shall let a clinician create a **Prescription** `RX-<yyyy>-…` with drug (Drug Master + ATC), dose, route, frequency, duration, quantity. | M | Structured medication orders. | Pharmacy |
| FR-RX-002 | The system shall check **drug–drug and drug–allergy interactions** at prescribing and warn/block per severity. | M | Patient safety (PBM). | Pharmacy |
| FR-RX-003 | The system shall route submitted prescriptions to the **Pharmacy queue** via domain event once `Approved`/`Active`. | M | Event-driven dispensing. | Pharmacy |
| FR-RX-004 | The system shall support **full dispense** of a prescription line, transitioning it toward `Dispensed`. | M | Core pharmacy action. | Pharmacy |
| FR-RX-005 | The system shall support **partial dispense** (e.g., stock shortage), recording quantity dispensed vs. remaining and setting `PartiallyDispensed`. | M | Real-world stock constraints. | Pharmacy |
| FR-RX-006 | The system shall support **generic/therapeutic substitution** within formulary rules, recording original vs. substituted drug and reason. | S | Formulary and stock realities (PBM). | Pharmacy |
| FR-RX-007 | The system shall enforce that **Pharmacies cannot see investigation/lab results** — only prescription and minimum beneficiary/eligibility context. | M | Hard data-minimization rule. | Cross-cutting |
| FR-RX-008 | The system shall enforce prescription lifecycle `Draft → Submitted → (Approved\|Rejected) → PartiallyDispensed → Dispensed` (+ `Expired`,`Cancelled`). | M | Canonical Rx state machine. | Pharmacy |
| FR-RX-009 | The system shall **atomically consume** each dispensed prescription line so the same authorized quantity cannot be dispensed twice (see §13). | M | Prevents double-dispense of benefits. | Cross-cutting |
| FR-RX-010 | The system shall validate dispense against **remaining coverage/limits** and prescribed quantity, blocking over-dispense. | M | Benefit integrity. | Pharmacy |
| FR-RX-011 | The system shall record **dispense events** with dispensing pharmacist, timestamp, batch/lot (if captured), and location. | S | Traceability & recall support. | Pharmacy |
| FR-RX-012 | The system shall let a pharmacy **flag a prescription for clarification** back to the prescriber without dispensing. | S | Safe handling of ambiguous orders. | Pharmacy |

---

## 7. Approvals / Authorizations (`AUTH`)

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-AUTH-001 | The system shall let an authorized requester **submit an authorization** `AUTH-<yyyy>-…` for a service requiring pre-approval, attaching clinical justification. | M | Gate high-cost/controlled services. | Approval |
| FR-AUTH-002 | The system shall present the **Medical Approval reviewer** a worklist of pending authorizations with SLA/TAT indicators. | M | Timely adjudication. | Approval |
| FR-AUTH-003 | The reviewer shall be able to view **EMR, clinical notes, and supporting reports** necessary to decide. | M | Approval role is permitted clinical visibility. | Approval |
| FR-AUTH-004 | The system shall support decisions `Approved \| PartiallyApproved \| Rejected \| InfoRequested`, each with mandatory reason and effective coverage. | M | Nuanced, auditable adjudication. | Approval |
| FR-AUTH-005 | The system shall support **emergency approval** — provisional authorization allowing service now, flagged for retrospective review. | M | Life/limb situations cannot wait. | Approval |
| FR-AUTH-006 | The system shall support **manual override** by the Medical Director with mandatory reason and elevated audit. | S | Governance escape hatch. | Approval |
| FR-AUTH-007 | The system shall track authorization lifecycle `Draft → Submitted → UnderReview → (Approved\|PartiallyApproved\|Rejected\|InfoRequested)` plus `Overridden`,`EmergencyApproved`,`Expired`. | M | Canonical authorization state machine. | Approval |
| FR-AUTH-008 | On approval, the system shall **unblock** the linked order/prescription/appointment automatically via domain event. | M | Removes manual re-entry, reduces delay. | Approval |
| FR-AUTH-009 | The system shall record and report **approval TAT** (submit → decision) per reviewer and service type. | S | SLA management. | Approval / Reporting |
| FR-AUTH-010 | `InfoRequested` decisions shall notify the requester and allow **resubmission** with added evidence, preserving the thread. | S | Efficient back-and-forth. | Approval |
| FR-AUTH-011 | The system shall enforce **separation of duties** so a clinician cannot approve their own authorization request. | M | Governance / anti-fraud. | Approval |

---

## 8. Provider Network (`NET`)

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-NET-001 | The system shall maintain **provider records** (Clinic, Doctor, Lab, Imaging Center, Pharmacy) with type, services offered, and status. | M | Network is the fulfillment backbone. | Cross-cutting |
| FR-NET-002 | The system shall model **contracts/agreements** linking providers to covered services, tariffs, and validity. | S | Benefit pricing and coverage scope. | Cross-cutting |
| FR-NET-003 | The system shall let **Provider Admin** manage their own staff accounts and locations within their organization boundary only. | M | Delegated, isolated administration. | Cross-cutting |
| FR-NET-004 | The system shall let the **Network Team** onboard, credential, suspend, and offboard providers with audit. | M | Network lifecycle governance. | Cross-cutting |
| FR-NET-005 | The system shall enforce **provider isolation** — each provider portal sees only its own queues and the minimum beneficiary data per task. | M | Hard privacy/isolation requirement. | Cross-cutting |
| FR-NET-006 | The system shall capture **provider capabilities/catalog** (which tests, modalities, formulary) used to route orders and referrals. | S | Correct routing. | Cross-cutting |
| FR-NET-007 | The system shall track provider **credentialing status and expiries** with reminders. | C | Compliance and quality. | Cross-cutting |

---

## 9. Notifications (`NOT`)

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-NOT-001 | The system shall send **event-driven notifications** (in-app, and where available SMS/email) for key transitions: appointment booked/reschedule/reminder, approval decision, result released, prescription ready. | M | Timely coordination for beneficiaries/staff. | Cross-cutting |
| FR-NOT-002 | Notifications shall be **bilingual** (AR/EN) honoring beneficiary/user language preference. | M | Localization requirement. | Cross-cutting |
| FR-NOT-003 | Notifications shall respect **data minimization** — no diagnoses or sensitive clinical detail in outbound SMS/email. | M | Privacy on insecure channels. | Cross-cutting |
| FR-NOT-004 | The system shall maintain a **notification/inbox center** per user with read/unread state. | S | Central, auditable comms. | Cross-cutting |
| FR-NOT-005 | The system shall support **configurable templates** and quiet-hours/rate limits. | C | Operational tuning. | Cross-cutting |
| FR-NOT-006 | The system shall log **delivery status** (sent/failed/retried) for each notification. | S | Observability & follow-up. | Cross-cutting |

---

## 10. Reporting & Analytics (`RPT`)

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-RPT-001 | The system shall provide an **executive dashboard** (volumes, eligibility mix, approval TAT, utilization, top diagnoses/services) with role-scoped visibility. | M | Leadership steering. | Cross-cutting |
| FR-RPT-002 | Reporting shall enforce **data minimization** — e.g., **Finance cannot see diagnoses**; clinical reports are role-gated. | M | Hard privacy rule. | Cross-cutting |
| FR-RPT-003 | The system shall provide **operational reports** per portal (Reception throughput, provider queues, pharmacy dispensing, approval backlog). | S | Day-to-day ops. | Cross-cutting |
| FR-RPT-004 | The system shall support **export** (CSV/PDF) with the exporter's permissions applied and export events audited. | S | Sharing & offline analysis. | Cross-cutting |
| FR-RPT-005 | The system shall provide **utilization vs. limit** reporting per policy/coverage to control spend. | S | Benefit management core value. | Cross-cutting |
| FR-RPT-006 | Aggregated/analytics datasets shall be **de-identified or pseudonymized** where individual identity is not required. | M | Privacy by design. | Cross-cutting |
| FR-RPT-007 | The system shall support **filtering by date, provider, service, and cohort** across reports. | S | Usable analytics. | Cross-cutting |

---

## 11. Admin, Identity & Access (`IAM`)

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-IAM-001 | The system shall authenticate users via the corporate IdP (OIDC/OAuth2) with **MFA** enforced. | M | Baseline security; see [18-security-model.md](18-security-model.md). | Cross-cutting |
| FR-IAM-002 | The system shall implement **RBAC + ABAC**, assigning users to one or more of the defined role portals. | M | Least privilege; portal separation. | Cross-cutting |
| FR-IAM-003 | The system shall render a **distinct portal (UI + permissions)** per role: Beneficiary Management, Reception, Call Center, Doctors, Nurses, Labs, Imaging Centers, Pharmacies, Medical Approval, Medical Director, Case Managers, Finance, Provider Admin, Network Team, Org Admin, Super Admin. | M | Core multi-portal model. | Cross-cutting |
| FR-IAM-004 | The system shall enforce **field- and row-level authorization** (need-to-know) on every read, default-deny. | M | Data minimization at the data layer. | Cross-cutting |
| FR-IAM-005 | **Org Admin** shall manage users, roles, and settings within their tenant; **Super Admin** shall manage tenants and platform-wide config. | M | Tiered administration. | Cross-cutting |
| FR-IAM-006 | The system shall support **session management** (timeout, revocation, concurrent-session policy) and device/conditional access. | S | Security hygiene. | Cross-cutting |
| FR-IAM-007 | The system shall provide **role/permission review** views for auditors (who can see/do what). | S | Governance & attestation. | Cross-cutting |
| FR-IAM-008 | The system shall support **tenant isolation** (Mersal = tenant 0; future orgs/donors as tenants) with no cross-tenant data leakage. | M | Multi-tenant readiness. | Cross-cutting |
| FR-IAM-009 | The system shall support **break-glass / emergency access** with heightened logging and post-hoc review. | S | Clinical continuity vs. strict access. | Cross-cutting |
| FR-IAM-010 | The system shall allow **de-provisioning** to immediately revoke a user's access across all portals. | M | Offboarding security. | Cross-cutting |

---

## 12. Master Data Management (`MDM`)

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-MDM-001 | The system shall maintain **ICD-10** diagnosis codes (structured for **ICD-11 readiness**) with search by code/term (AR/EN). | M | Coded diagnoses; interoperability. | Consultation |
| FR-MDM-002 | The system shall maintain **CPT** procedure codes for orders/procedures. | M | Codeable services & billing readiness. | Lab & Imaging |
| FR-MDM-003 | The system shall be **LOINC-ready** for lab observation coding. | S | Lab interoperability. | Lab & Imaging |
| FR-MDM-004 | The system shall maintain a **Drug Master with ATC classification**. | M | Prescribing & PBM. | Pharmacy |
| FR-MDM-005 | The system shall maintain a **drug–drug interaction** knowledge base used at prescribing. | M | Patient safety. | Pharmacy |
| FR-MDM-006 | The system shall maintain an **allergy database** used for drug–allergy checking. | M | Patient safety. | Pharmacy |
| FR-MDM-007 | The system shall version master data and support **effective-dated** updates without breaking historical records. | S | Codes change over time. | Cross-cutting |
| FR-MDM-008 | The system shall restrict master-data editing to authorized roles (e.g., Super Admin / clinical governance) with audit. | M | Data quality & governance. | Cross-cutting |
| FR-MDM-009 | The system shall support **formulary** definition (covered drugs, substitution rules) referenced by pharmacy dispensing. | S | PBM/benefit control. | Pharmacy |
| FR-MDM-010 | Master-data lookups shall be **bilingual and searchable** with typo tolerance. | S | Usability for AR/EN staff. | Cross-cutting |

---

## 13. Order & Prescription Consumption Invariants (`INV`) — first-class FRs

> These express the **anti-double-spend guarantees** from [0A §7](0A-DESIGN-FOUNDATIONS.md) as explicit, testable requirements. They are cross-cutting and **Must**.

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-INV-001 | **Atomic consumption:** consuming an order line or dispensing a prescription line shall be a single atomic transaction; either the whole consumption commits or none of it does. | M | No partial/torn benefit usage. | Cross-cutting |
| FR-INV-002 | **Uniqueness constraint:** the data model shall enforce a unique constraint equivalent to `(order_line_id WHERE status = consumed)` (and likewise for prescription lines) so a line can be consumed **at most once**. | M | Makes duplicate usage *impossible*, not merely unlikely. | Cross-cutting |
| FR-INV-003 | **Optimistic concurrency:** concurrent consume attempts on the same line shall be resolved so exactly one succeeds; the loser receives a deterministic conflict response. | M | Two providers/tabs cannot both win. | Cross-cutting |
| FR-INV-004 | **Idempotency:** repeated consume/dispense requests carrying the same idempotency key shall not double-apply; the original result is returned. | M | Safe retries over unreliable networks. | Cross-cutting |
| FR-INV-005 | **Quantity conservation:** total dispensed quantity across all dispense events for a prescription line shall never exceed the authorized/prescribed quantity. | M | Benefit integrity for partial dispensing. | Pharmacy |
| FR-INV-006 | **Coverage decrement:** consuming/dispensing shall decrement remaining coverage/limits within the same transaction as the consumption, never as a separate best-effort step. | M | Limits and usage cannot drift apart. | Cross-cutting |
| FR-INV-007 | **Immutable consumption record:** each consumption/dispense shall write an append-only, hash-chained audit event (who/what/when/where). | M | Non-repudiation; see [19-audit-strategy.md](19-audit-strategy.md). | Cross-cutting |
| FR-INV-008 | **Reversal only via compensating action:** an erroneous consumption shall be corrected by an audited reversal/void event, never by mutating or deleting the original. | M | Preserves history and traceability. | Cross-cutting |
| FR-INV-009 | **State-guarded transitions:** consumption shall be permitted only from valid source states (`Active`/`PartiallyUsed` for orders; `Approved`/`Active`/`PartiallyDispensed` for Rx); all others are rejected. | M | Aligns with [23-state-machines.md](23-state-machines.md). | Cross-cutting |
| FR-INV-010 | **Provider ownership:** a line may be consumed only by the provider to whom it is routed/assigned. | M | Provider isolation + integrity. | Cross-cutting |

---

## 14. Audit (`AUD`)

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-AUD-001 | The system shall record an **immutable, append-only, hash-chained audit event** for every create/read-sensitive/update/consume/approve/export action. | M | Tamper-evident accountability. | Cross-cutting |
| FR-AUD-002 | Audit events shall capture **actor, role, tenant, action, target, timestamp (UTC), source IP/session, and outcome**. | M | Investigable trail. | Cross-cutting |
| FR-AUD-003 | The system shall **not hard-delete** clinical/benefit data; changes use soft-delete + history tables. | M | Medico-legal retention. | Cross-cutting |
| FR-AUD-004 | The system shall record **access to EMR/PHI reads** (not just writes) to evidence minimum-necessary compliance. | M | HIPAA-style access logging. | Cross-cutting |
| FR-AUD-005 | Authorized auditors shall be able to **query/replay** the audit trail for a beneficiary, provider, or user. | S | Investigations & attestations. | Cross-cutting |
| FR-AUD-006 | Audit records shall be **retained** per policy and protected from modification even by admins. | M | Integrity of the record. | Cross-cutting |
| FR-AUD-007 | The system shall alert on **anomalous access patterns** (e.g., bulk EMR reads, off-hours access). | C | Insider-threat detection. | Cross-cutting |

---

## 15. Priority summary (MoSCoW rollup)

| Module | Must | Should | Could/Won't |
|--------|------|--------|-------------|
| Registration/Policy | 10 | 3 | 1 |
| Eligibility | 6 | 3 | 0 |
| Appointments | 8 | 3 | 0 |
| Clinical/EMR | 9 | 4 | 0 |
| Lab & Imaging | 8 | 3 | 0 |
| Pharmacy | 8 | 4 | 0 |
| Approvals | 8 | 3 | 0 |
| Provider Network | 4 | 2 | 1 |
| Notifications | 3 | 2 | 1 |
| Reporting | 4 | 3 | 0 |
| Admin/Identity | 7 | 3 | 0 |
| Master Data | 5 | 4 | 0 |
| Consumption Invariants | 10 | 0 | 0 |
| Audit | 5 | 1 | 1 |

> The **Consumption Invariants** and **Data-Minimization** FRs are release-gating: no MVP is acceptable without them ([28-mvp-definition.md](28-mvp-definition.md)).

---

### Cross-references
- Non-functional targets: [08-non-functional-requirements.md](08-non-functional-requirements.md)
- Screen realization: [12-ui-wireframes.md](12-ui-wireframes.md) · Flows: [13-ux-flows.md](13-ux-flows.md)
- Who can do each FR: [10-role-matrix.md](10-role-matrix.md) · [11-permission-matrix.md](11-permission-matrix.md)
- Lifecycles referenced: [23-state-machines.md](23-state-machines.md)
