# 07 — Functional Requirements

> Back to [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [08-non-functional-requirements.md](08-non-functional-requirements.md) · [09-information-architecture.md](09-information-architecture.md) · [10-role-matrix.md](10-role-matrix.md) · [11-permission-matrix.md](11-permission-matrix.md) · [23-state-machines.md](23-state-machines.md)

This document specifies the **functional requirements (FRs)** for the Mersal HBMP. Requirements are grouped by module, each carrying:

- **ID** — `FR-<MOD>-nnn` (stable, never reused).
- **Statement** — the observable capability, written as "The system shall…".
- **Priority** — MoSCoW: **M** = Must, **S** = Should, **C** = Could, **W** = Won't (this release).
- **Rationale** — why it exists.
- **Phase** — which of the 7 care phases it primarily serves: `Registration`, `Eligibility`, `Appointments`, `Consultation`, `Lab & Imaging`, `Pharmacy`, `Approval` (or `Cross-cutting`).

**Module codes:** `REG` Registration/Policy · `ELG` Eligibility · `APT` Appointments · `CLIN` Clinical/EMR · `LAB` Lab & Imaging · `RX` Pharmacy · `AUTH` Approvals · `NET` Provider Network · `NOT` Notifications · `RPT` Reporting · `IAM` Admin/Identity · `MDM` Master Data · `AUD` Audit · `CLM` Claims Management · `BRN` Branch Scoping & Practitioner Specialty · `SEN` Clinical Sensitivity.

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

## 15. Claims Management (`CLM`)

> **New module — build phase `10b`** ([36-claims-management.md](36-claims-management.md) is the authoritative design; build prompt: [claude-code-prompts/phase-10b-claims-management.md](claude-code-prompts/phase-10b-claims-management.md)). Claims turn **already-delivered, authorized services** into reviewed, decided and settled financial records. Because claims sit downstream of fulfillment rather than inside the 7 care phases, the **Phase** column below reads `Claims (10b)` / `Cross-cutting (10b)`.
>
> Two hard boundaries govern every requirement here: **(a)** claims are adjudicated on **codes and amounts, never on diagnosis** — medical-necessity questions are routed to a clinical reviewer; **(b)** the platform **issues settlement advice but never moves money**.

**15.1 Claim origination — three channels**

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-CLM-001 | The system shall **auto-derive** a claimable item from each `order_fulfillment` (consume) and `dispense_event` row — consuming `OrderLinesConsumed` / `RxLinesDispensed` events — priced from the performing provider's contract tariff. | M | Fulfillment records are the authoritative usage truth; auto-derivation is the baseline of what the network actually delivered. | Claims (10b) |
| FR-CLM-002 | The system shall accept a **provider-submitted** claim/invoice with supporting documents, and shall **match** each submitted line to an auto-derived claimable item on `(provider, beneficiary, service code, service date, authorization)`. | M | Providers bill in their own cycle; matching reconciles their bill to our record. | Claims (10b) |
| FR-CLM-003 | A submitted line that cannot be matched shall be flagged `NO_FULFILLMENT_RECORD` and routed to **manual assessment**; it shall **never** be auto-approved. | M | Billing for undelivered service is the primary claims fraud/error vector. | Claims (10b) |
| FR-CLM-004 | The system shall record the provider's **billed amount alongside the contract price** on every matched line and surface any difference as a **price-variance adjustment candidate**. | M | Variance must be visible and actionable, not silently absorbed. | Claims (10b) |
| FR-CLM-005 | The system shall accept a **beneficiary reimbursement request** for out-of-pocket services — submitted by the member, or by Reception/a Case Manager on their behalf — carrying **receipts** plus **proof-of-service** evidence (result/dispense evidence). | M | Members do pay out of pocket; reimbursement is the third, human-heavy origination channel. | Claims (10b) |
| FR-CLM-006 | A reimbursement request shall reference an **authorized** underlying prescription or investigation order (or an explicitly configured non-gated category); otherwise it shall be denied `NO_PRIOR_AUTH` or routed to manual assessment. | M | Reimbursement must not become a bypass of the authorization gate. | Claims (10b) |
| FR-CLM-007 | Reimbursement shall be capped at the **contract tariff or the receipt amount, whichever is lower**, unless a Claims Officer records an explicit **override with justification** (subject to dual control above threshold, FR-CLM-050). | M | Prevents out-of-network prices leaking into the benefit spend. | Claims (10b) |
| FR-CLM-008 | The system shall **not store** the beneficiary's bank/payout details on the claim; settlement advice shall reference the member only, with disbursement handled by Mersal's existing finance process. | M | Data minimization; payout data has no place in a benefit record. | Claims (10b) |
| FR-CLM-009 | All claim/reimbursement documents shall be **virus-scanned, type/size-validated, typed, encrypted and stored in `document-service`**, referenced from the claim. | M | Untrusted uploads from providers and members; see [18-security-model.md](18-security-model.md). | Cross-cutting (10b) |

**15.2 OCR-assisted reimbursement intake**

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-CLM-010 | The system shall run submitted reimbursement documents through an **OCR extraction pipeline** behind the pluggable `IDocumentOcrProvider` interface, extracting candidate **provider, service date, amount, currency, and service/drug codes**. | M | Manual keying of receipts is slow and error-prone at refugee-caseload volume. | Claims (10b) |
| FR-CLM-011 | Every OCR-extracted value shall carry a **confidence score** and a reference to its **source document region**, both persisted with the candidate. | M | A number without provenance cannot be trusted with money. | Claims (10b) |
| FR-CLM-012 | **OCR shall be assistive, never authoritative:** no OCR-extracted value shall affect an amount, a match, or a decision until a **human has confirmed it**. | M | Machine reading of a smudged Arabic receipt must never silently become a payment. | Claims (10b) |
| FR-CLM-013 | Where extraction confidence is high and the candidate **auto-matches** an authorized prescription/investigation order, the system shall pre-fill claim lines flagged `AUTO_MATCHED`; where confidence is low or the match is ambiguous, the request shall be routed to the **manual assessment queue**. | M | Automate the easy majority, route the ambiguous remainder to a human. | Claims (10b) |
| FR-CLM-014 | The OCR engine shall support **Arabic and English** (e.g. Tesseract `ara+eng`), be **self-hosted**, and shall not transmit any document outside the platform boundary. | M | Bilingual receipts are the norm; PHI/PII must not leave the on-prem boundary ([0C](0C-OPEN-SOURCE-STACK.md)). | Cross-cutting (10b) |
| FR-CLM-015 | A reviewer performing manual assessment shall see the **document image with the OCR overlay** (extracted field, confidence, highlighted region) and shall be able to correct any value, with the correction audited. | M | Human confirmation must be cheap and verifiable against the source. | Claims (10b) |
| FR-CLM-016 | The system shall report **OCR auto-match rate** and **manual-assessment rate** as operational KPIs. | S | Tunes the confidence threshold and sizes the assessment team. | Claims (10b) / Reporting |

**15.3 Batching**

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-CLM-017 | The system shall create a claim **batch** by any of: **date range** (`serviceDateFrom..serviceDateTo`, optionally `receivedDate`), **provider branch** (`provider_location`), **provider group** (parent provider across branches), or **manual selection** from a filtered worklist. | M | Different payees settle on different cycles; exceptions need hand-picking. | Claims (10b) |
| FR-CLM-018 | The system shall enforce **single open-batch membership** — a claim shall belong to at most one batch whose status is `Open` or `UnderReview` — via a unique partial index, not application logic alone. | M | Makes settling the same claim twice *impossible*, not merely unlikely. | Cross-cutting (10b) |
| FR-CLM-019 | Settlement batches shall be **provider-homogeneous** (one payee); reimbursement batches shall group by period with the payee being the beneficiary cohort. | M | A settlement advice must address exactly one payee. | Claims (10b) |
| FR-CLM-020 | Each batch shall carry running **rollup totals**: claimed, priced, approved, adjusted, denied, and **net payable**. | M | The batch is the unit of review and settlement; its totals must always be current. | Claims (10b) |
| FR-CLM-021 | The system shall allow a claim to be **removed from an `Open` batch** (audited); removal from an `UnderReview` batch shall require a **mandatory reason** and shall be audited as an exception. | M | Late corrections happen; they must leave a trail. | Claims (10b) |
| FR-CLM-022 | The system shall issue a human-readable **batch number** `BAT-<yyyy>-<base32(8)>` per [0A §3](0A-DESIGN-FOUNDATIONS.md). | M | Speakable key for finance and provider correspondence. | Claims (10b) |
| FR-CLM-023 | The system shall enforce the batch lifecycle `Open → UnderReview → Decided → SettlementIssued → Closed` (plus `Cancelled`); a batch shall reach **`Decided` only when every line has a recorded decision**, and rollup totals shall be **frozen at `SettlementIssued`**. | M | No half-decided batch may be settled; frozen totals make the advice immutable. | Claims (10b) |

**15.4 Automated pre-adjudication**

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-CLM-024 | The system shall run automated **pre-adjudication per claim line in a fixed 9-step order**: (1) beneficiary status & policy validity on the service date, (2) coverage-category match, (3) pre-auth linkage, (4) fulfillment linkage, (5) duplicate check, (6) provider network status & contract effectivity, (7) tariff pricing, (8) coverage-limit availability, (9) co-pay/deductible split. | M | A fixed, versioned order makes adjudication deterministic, testable and explainable. | Claims (10b) |
| FR-CLM-025 | Pre-adjudication shall **collect all applicable reason codes** rather than stopping at the first failure. | M | Partial approvals must be precise, and providers must get one complete answer, not a drip of denials. | Claims (10b) |
| FR-CLM-026 | Every failed check shall emit a **coded reason** from the controlled set: `NOT_ELIGIBLE`, `POLICY_EXPIRED`, `NOT_COVERED_CATEGORY`, `NO_PRIOR_AUTH`, `AUTH_EXPIRED`, `EXCEEDS_AUTH_SCOPE`, `NO_FULFILLMENT_RECORD`, `DUPLICATE_CLAIM`, `PROVIDER_OUT_OF_NETWORK`, `CONTRACT_NOT_EFFECTIVE`, `NO_TARIFF`, `LIMIT_EXCEEDED`. | M | Free-text denials cannot be reported on, appealed against, or tested. | Claims (10b) |
| FR-CLM-027 | Where no contract tariff exists for the code on the service date, the system shall emit `NO_TARIFF` and route the line to **manual pricing** — it shall **never infer, estimate or guess a price**. | M | A guessed price is an unauditable payment; a human must own it. | Claims (10b) |
| FR-CLM-028 | Where the linked authorization is `PartiallyApproved`, the approved scope shall **cap** the payable lines/quantities, emitting `EXCEEDS_AUTH_SCOPE` for the excess. | M | Partial approvals must bind the money, not just the clinical permission. | Claims (10b) |
| FR-CLM-029 | Pre-adjudication shall output, per line, a `system_recommendation` ∈ {`RecommendApprove`, `RecommendPartial`, `RecommendDeny`, `RequiresManualReview`} plus reason codes and a computed `allowed_amount`. **The system recommends; the Claims Officer decides.** | M | Keeps accountability with a human while automating the analysis. | Claims (10b) |
| FR-CLM-030 | Auto-approval of clean, low-value lines shall be **configurable per policy and OFF by default**, and shall **never** apply to gated, high-value, reimbursement, or `RequiresManualReview` lines. | M | Automation is an efficiency lever, not a control bypass. | Claims (10b) |
| FR-CLM-031 | Adjudication rules shall be expressed **declaratively and versioned**, and the **rule version shall be recorded on every decision**. | M | A decision must be reproducible against the rules in force at the time. | Cross-cutting (10b) |

**15.5 Review & decision**

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-CLM-032 | The system shall support **line-level Claims Officer decisions**: **Approve · Partially approve** (with allowed amount) **· Deny** (coded reason mandatory) **· Adjust · Request info · Route to clinical review**. | M | The claim line, not the invoice, is the true unit of adjudication. | Claims (10b) |
| FR-CLM-033 | Line decisions shall **roll up to the batch**, recomputing batch totals on every decision; the batch outcome (`Approved` / `PartiallyApproved` / `Denied`) shall be derived from its lines, never entered directly. | M | One consistent number for settlement, derived from evidence. | Claims (10b) |
| FR-CLM-034 | Denials and partial approvals shall require a **coded reason**; free-text **rationale shall be mandatory** for deny, adjust and override decisions. | M | Appeals, reporting and audit all depend on structured reasons. | Claims (10b) |
| FR-CLM-035 | The Claims Officer workspace shall present, per line: service code + description, service date, provider/branch, billed amount, contract price, system recommendation + reason codes, linked authorization, fulfillment reference, and supporting documents (invoice, receipt, proof-of-service, OCR overlay) — and **shall present no diagnosis, no clinical note, and no lab/imaging result value**. | M | Minimum-necessary is enforced in the projection, not in training. | Cross-cutting (10b) |
| FR-CLM-036 | The system shall support **Request info**, moving the line/claim to `PendingInfo`, notifying the submitter, and resuming adjudication on supply of information while **preserving the thread**. | M | Most disputes are missing-document problems, not judgement problems. | Claims (10b) |
| FR-CLM-037 | The system shall support **Route to clinical review**, transitioning the line to `ClinicalReview` and placing it in the **Medical Approval / Medical Director** worklist; the clinical reviewer shall see the clinical context under purpose binding and **record an opinion**, and shall **not** make the payment decision. | M | Medical-necessity judgement needs a clinician; the payment decision stays with claims. | Claims (10b) / Approval |
| FR-CLM-038 | Routing a line to clinical review shall **not widen the Claims Officer's field projection**, and the returned clinical opinion shall be surfaced to the officer as a **structured verdict + rationale only**. | M | The hand-off must not become a back door into the EMR. | Cross-cutting (10b) |
| FR-CLM-039 | Every decision shall record **decider, timestamp, decision, allowed amount, reason code(s), rationale, rule version and correlation id**, appended to `claim_decision`. | M | Non-repudiation of every financial judgement. | Claims (10b) |
| FR-CLM-040 | Submitted claims and recorded decisions shall be **append-only** — never edited or deleted. Corrections shall be made by an **adjustment** or a compensating **Void + re-claim**. | M | Financial history must be reconstructable; see [19-audit-strategy.md](19-audit-strategy.md). | Cross-cutting (10b) |
| FR-CLM-041 | The system shall enforce the claim lifecycle `Draft → Submitted → UnderAdjudication → (PendingInfo \| ClinicalReview) → (Approved \| PartiallyApproved \| Denied) → Settled`, plus `Appealed` and `Void`, per [23-state-machines.md](23-state-machines.md). | M | Canonical claim state machine. | Claims (10b) |

**15.6 Reconciliation & adjustments**

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-CLM-042 | The system shall provide a **reconciliation view** for a period comparing three records: what Mersal's fulfillment data says was **delivered**, what the provider **billed**, and what was **approved for payment**. | M | Three views of the same month must be made to agree, visibly. | Claims (10b) |
| FR-CLM-043 | Reconciliation shall bucket every discrepancy as **matched**, **billed-not-delivered**, **delivered-not-billed**, **price variance**, **quantity variance**, or **duplicate**, each actionable from the worklist. | M | Named buckets turn a spreadsheet argument into a workflow. | Claims (10b) |
| FR-CLM-044 | The system shall report **aged delivered-not-billed** (unbilled delivered service) so accruals and provider chasing are possible. | S | Protects the budget picture from lagging provider invoicing. | Claims (10b) / Reporting |
| FR-CLM-045 | The system shall support the adjustment types `PriceCorrection`, `QuantityCorrection`, `Deduction`, `Recovery`/`Clawback`, `Writeoff`, `Reversal`/`Void`, and `Reallocation`. | M | Covers the full set of real-world corrections without ad-hoc edits. | Claims (10b) |
| FR-CLM-046 | Every adjustment shall be **append-only**, carry a **sign (debit/credit)**, a **coded reason**, a **mandatory rationale**, and shall **net into the batch rollup**; adjustments shall never mutate a prior amount. | M | Corrections must add to the record, not overwrite it. | Cross-cutting (10b) |
| FR-CLM-047 | A `Recovery`/`Clawback` shall **reference the original claim line** it recovers against and may be carried into a later batch. | M | Overpayments are recovered across periods and must stay traceable. | Claims (10b) |
| FR-CLM-048 | A batch **net payable shall not fall below zero** without an explicit, **dual-controlled** approval. | M | A negative settlement is an exceptional financial event. | Claims (10b) |
| FR-CLM-049 | Every adjustment shall be audited with **before/after amounts**, actor, reason and correlation id. | M | Adjustments are the highest-risk money-moving action in the module. | Cross-cutting (10b) |

**15.7 Settlement advice, exports & appeals**

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-CLM-050 | On batch `Decided`, the system shall generate an **immutable settlement advice / remittance statement** per payee containing: header (payee provider/branch or reimbursement cohort, period, batch no., generated-by, generated-at), per-claim/line detail (approved, adjusted, denied with reason codes), and the totals chain **claimed → priced → approved → adjustments → net payable**. | M | The single, defensible hand-off artifact to finance and to the provider. | Claims (10b) |
| FR-CLM-051 | The settlement advice shall be stored as a stable document in `document-service` on a **WORM/object-locked** bucket and referenced from the batch. | M | Tamper-evidence for a financial statement. | Claims (10b) |
| FR-CLM-052 | The system shall support **audited exports** of claims data and settlement advice — **CSV/XLSX** for finance and **PDF** for the provider — carrying **no clinical fields**, with each export written as a high-severity `data.export` audit event. | M | Sharing must be traceable and must not leak PHI. | Cross-cutting (10b) |
| FR-CLM-053 | The system shall **not execute payments or bank transfers**. Disbursement is performed externally by Finance/treasury; the system may optionally record a **payment reference** back against the batch. | M | Explicit scope boundary — the platform is a benefit system, not a payment rail. | Claims (10b) |
| FR-CLM-054 | The system shall support **appeals**: a provider or member may appeal an `Approved`, `PartiallyApproved` or `Denied` claim, transitioning it to `Appealed` and back to `UnderAdjudication` for re-adjudication, with the **original decision preserved**. | M | Due process for providers and members; denials must be contestable. | Claims (10b) |
| FR-CLM-055 | An appeal shall be **decided by a different principal** than the original decider. | M | An appeal reviewed by its author is not an appeal. | Claims (10b) |

**15.8 Integrity, segregation of duties & audit**

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-CLM-056 | **No double-billing:** the data model shall enforce a unique constraint of **one payable claim line per fulfillment/dispense reference**; a duplicate submission shall be detected and denied `DUPLICATE_CLAIM`. | M | Makes paying twice for one delivered service structurally impossible. | Cross-cutting (10b) |
| FR-CLM-057 | **No re-decrement of coverage:** claims shall reconcile against the existing `consumed_value` accumulator moved by the consume/dispense transaction, and shall **never** decrement coverage/limits again or maintain a parallel accumulator. | M | Claims are downstream of fulfillment; a second decrement would corrupt every remaining-limit answer ([FR-INV-006](#13-order--prescription-consumption-invariants-inv--first-class-frs)). | Cross-cutting (10b) |
| FR-CLM-058 | **Segregation of duties:** the principal deciding a claim line shall **not** be its originator/submitter, and shall **not** be affiliated with the claiming provider. | M | Self-adjudication and provider self-payment are the two classic claims frauds. | Cross-cutting (10b) |
| FR-CLM-059 | **Adjudication ≠ settlement release:** the principal who adjudicated a claim/batch shall not be the principal who releases its payment in the finance process. | M | Preserves the existing initiate ≠ release split end-to-end. | Cross-cutting (10b) |
| FR-CLM-060 | **Dual control:** overrides above a configurable value threshold and high-value adjustments shall require approval by a **second, senior approver** (Claims Reviewer) who did not record them. | M | Large discretionary amounts must never rest on one signature. | Cross-cutting (10b) |
| FR-CLM-061 | **Minimum-necessary projection:** claims projections shall strip `diagnosis`, `emr_note`, and lab/imaging **result values** server-side; result **existence, date and document reference** may be exposed as proof-of-service. | M | Hard privacy rule; see [11-permission-matrix.md §3.4/§4](11-permission-matrix.md). | Cross-cutting (10b) |
| FR-CLM-062 | **Provider isolation:** a provider-side principal shall access only **its own** claims, lines, batches, documents and settlement advice. | M | Hard isolation rule ([FR-NET-005](#8-provider-network-net)). | Cross-cutting (10b) |
| FR-CLM-063 | **Member scope:** a beneficiary shall access only their **own** reimbursement request and its outcome; a Case Manager only those of their **assigned** case load. | M | Members must not see other members' claims or provider batches. | Cross-cutting (10b) |
| FR-CLM-064 | **Idempotency:** claim submit, line decide, adjust and batch-add operations shall accept an `Idempotency-Key` and shall not double-apply on retry. | M | Money-moving operations over unreliable networks. | Cross-cutting (10b) |
| FR-CLM-065 | **Immutable audit:** every claim state change, decision, adjustment, void, batch transition, settlement issuance and export shall write an **append-only, hash-chained** audit event; **nothing in claims shall be hard-deleted**. | M | Tamper-evident financial accountability. | Cross-cutting (10b) |
| FR-CLM-066 | The system shall publish claims **domain events via the transactional outbox** (`ClaimCreated`, `ClaimSubmitted`, `ClaimAdjudicated`, `ClaimLineDecided`, `ClaimApproved`, `ClaimPartiallyApproved`, `ClaimDenied`, `ClaimAdjusted`, `ClaimVoided`, `ClaimAppealed`, `ReimbursementSubmitted`, `ReimbursementMatched`, `ReimbursementRequiresManualAssessment`, `BatchCreated`, `BatchUnderReview`, `BatchDecided`, `SettlementAdviceIssued`). | M | Reporting, notification and finance integration without coupling. | Cross-cutting (10b) |
| FR-CLM-067 | The system shall report claims **KPIs**: TAT (submission → decision), approval/denial rate, top denial reasons, adjustment value by type, provider variance league table, batch cycle time, and recovery outstanding. | S | Steering the claims operation and the provider network. | Claims (10b) / Reporting |
| FR-CLM-068 | All claims screens shall be **bilingual (Arabic RTL + English)** and meet **WCAG 2.2 AA**, including non-colour encoding of decision/variance status. | M | Platform-wide a11y/i18n requirement ([21-accessibility-checklist.md](21-accessibility-checklist.md)). | Cross-cutting (10b) |

---

## 16. Branch Scoping, Practitioner Specialty (`BRN`) & Clinical Sensitivity (`SEN`)

> **New module — build phase `14`** ([37-branch-scoping-and-clinical-sensitivity.md](37-branch-scoping-and-clinical-sensitivity.md) is the authoritative design; build prompt: [claude-code-prompts/phase-14-branch-scoping-and-sensitivity.md](claude-code-prompts/phase-14-branch-scoping-and-sensitivity.md)). Two capabilities are added to the running platform: **multi-branch (location) awareness with practitioner specialty**, and **sensitivity-gated clinical results** with a justified release-request workflow. Because both are cross-cutting overlays rather than a new care phase, the **Phase** column reads `Branch (14)` / `Sensitivity (14)` / `Cross-cutting (14)`.
>
> Two hard boundaries govern every requirement here: **(a)** branch scoping is an **additional narrowing filter, never a replacement** for an existing control (treating-relationship, provider-ownership and minimum-necessary field rules are unchanged); **(b)** a **sensitive result is default-deny** — only the authoring/ordering doctor sees its content, and everyone else, **including the medical approval team**, sees existence metadata until a justified release is granted.

**16.1 Branch entity & staff assignment (`BRN`)**

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-BRN-001 | The system shall maintain an internal **`branch`** entity (code, EN/AR name, city, address, timezone, phone, opening hours, status ∈ {`Active`,`Suspended`,`Closed`}) representing a Mersal-operated facility. | M | Mersal delivers care from its own sites; the platform had no internal org-unit concept. | Branch (14) |
| FR-BRN-002 | The system shall **seed the six branches** `ASW` Aswan, `ALX` Alexandria, `OCT` 6th of October, `MAA` Maadi, `DOK` Dokki, `NSR` Nasr City with bilingual (AR/EN) names, all `Africa/Cairo`. | M | These are the operating sites; branch codes are stable keys used in business keys and reporting. | Branch (14) |
| FR-BRN-003 | A `branch` shall be **distinct from a contracted `provider_location`**: only branches are subject to staff branch-scoping, and no provider-side role shall administer or enumerate branches. | M | Conflating an internal facility with a contracted third-party site would leak Mersal's org structure and break provider isolation. | Branch (14) |
| FR-BRN-004 | The system shall support **branch status transitions** (`Active → Suspended → Active`, `→ Closed`) with reason and audit; a non-`Active` branch shall accept no new bookings while remaining readable for history. | S | Sites open, pause and close; historical records must survive. | Branch (14) |
| FR-BRN-005 | The system shall assign a user to branches via **`user_branch_assignment`** with `assignment_type` ∈ {`Home`,`Additional`}, a validity window and a status. | M | Staff have a base site and may cover others — the confirmed "can also work elsewhere" requirement. | Branch (14) |
| FR-BRN-006 | The system shall enforce **exactly one active `Home` branch per user** by a database partial unique index — `(user_id) WHERE assignment_type='Home' AND status='Active'` — not by application logic alone. | M | Makes an ambiguous default context *impossible*, not merely unlikely. | Cross-cutting (14) |
| FR-BRN-007 | The system shall compute a user's **permitted branch set** as `Home ∪ Additional`, filtered to `status='Active'` and within the validity window, re-evaluated **on every request**. | M | Revocation must take effect immediately, without waiting for token expiry. | Cross-cutting (14) |
| FR-BRN-008 | **No user shall create, extend or approve their own branch assignment.** `user_branch_assignment` shall be maintained by **Org Admin** and `practitioner_branch_assignment` by the **Network Team**, with every change audited (actor, subject, branch, type, validity, justification). | M | Self-granted scope is self-granted access; see [10 §7](10-role-matrix.md). | Cross-cutting (14) |
| FR-BRN-009 | Revocation of a branch assignment shall take effect **immediately** on the next request, and shall be audited and notified to the affected user. | M | A revoked assignment that lingers is an open door. | Cross-cutting (14) |

**16.2 Active-branch context (`BRN`)**

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-BRN-010 | The client shall declare the working context with an **`X-Active-Branch: <branch_id>`** header; when the header is **absent the service shall default to the user's `Home` branch**. | M | One unambiguous working context, with a safe default. | Cross-cutting (14) |
| FR-BRN-011 | The service shall **validate the active branch against the permitted set on every request** and, where it is outside that set, return **`403`** with an RFC 7807 problem document and write an audited **`BranchScopeDenied`** event. **The header shall never be trusted.** | M | The header is a client hint; authorization is a server decision. | Cross-cutting (14) |
| FR-BRN-012 | Switching the active branch shall emit an audited **`ActiveBranchSwitched`** event carrying actor, from-branch, to-branch and correlation id. | M | Which site a user was working in is material to every downstream action. | Cross-cutting (14) |
| FR-BRN-013 | Every response shall **echo the active branch** so the UI can display the current context unambiguously. | S | A user must never mistake which site they are working in. | Cross-cutting (14) |
| FR-BRN-014 | The system shall expose the user's **permitted branch list** (with the `Home` branch marked) to the branch switcher, and nothing beyond it. | M | The switcher is a scoped picker, not a directory of the organization. | Branch (14) |

**16.3 Scope modes & branch-scoped worklists (`BRN`)**

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-BRN-015 | Every role shall declare exactly one **scope mode** — `BranchScoped`, `MemberScoped` or `ProviderScoped` — in its policy bundle ([10 §2](10-role-matrix.md), [11 §5](11-permission-matrix.md)). | M | The mode is the contract that makes filtering testable rather than incidental. | Cross-cutting (14) |
| FR-BRN-016 | For **BranchScoped** roles — Reception, Appointment Coordinator, Nurse, Doctor *(operational lists)*, Branch/Clinic Manager — the system shall filter **server-side to the active branch**: appointment lists, the reception queue/day-list, encounters and branch-originated orders. | M | Branch relevance must be enforced, not left to a client-side filter. | Branch (14) |
| FR-BRN-017 | A BranchScoped request for a record belonging to **another branch shall be denied** (`403` + audited), **not returned as an empty result set**. | M | "Empty" leaks nothing but teaches nothing; denial is the testable, auditable behaviour. | Cross-cutting (14) |
| FR-BRN-018 | For **MemberScoped** roles — Medical Approval, Medical Director, Case Manager, Finance, Claims Officer/Reviewer, Network Team, Org/Super Admin, managers and reporting — work shall span **all branches by default**, with any branch filter offered as a **convenience only, never a restriction**. | M | Beneficiaries move between sites; member-centred work must not be fragmented by geography. | Cross-cutting (14) |
| FR-BRN-019 | **ProviderScoped** queues (external labs, imaging centres, pharmacies) shall be **unchanged** by branch scoping and shall continue to be scoped by provider-ownership. | M | Mersal's internal branch dimension is not a contracted provider's concern. | Cross-cutting (14) |
| FR-BRN-020 | Branch scoping shall be an **additional narrowing filter** that never replaces an existing control: treating-relationship, provider-ownership, assignment and minimum-necessary field rules shall continue to apply unchanged. | M | A narrowing filter that silently becomes a grant is a privacy regression. | Cross-cutting (14) |
| FR-BRN-021 | Branch-scoped tables shall optionally carry a **PostgreSQL RLS predicate** on `branch_id` (session GUC) as defence in depth, mirroring the proven `provider_id` RLS pattern. | C | Belt-and-braces against a missed service-layer predicate. | Cross-cutting (14) |

**16.4 Practitioner, specialty & scheduling (`BRN`)**

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-BRN-022 | The system shall maintain a **`practitioner`** record — the clinical profile behind a user — with `practitioner_type` ∈ {`Doctor`,`Nurse`}, EN/AR name, licence number, licence expiry and status. | M | Clinical identity was previously implicit in the user account. | Branch (14) |
| FR-BRN-023 | The system shall maintain a bilingual **`specialty`** reference list (General Practice, Internal Medicine, Pediatrics, OB/GYN, Cardiology, Dermatology, **Psychiatry**, **Clinical Psychology**, Neurology, Orthopaedics, ENT, Ophthalmology, Endocrinology, Gastroenterology, Nephrology, Pulmonology, Urology, Oncology, Rheumatology, General Surgery, Emergency Medicine, Radiology, Pathology, Physiotherapy, Nutrition, Dentistry). | M | Referral routing, reporting and examination-type suggestion all need structured specialty. | Branch (14) |
| FR-BRN-024 | A practitioner shall carry **one or more specialties with exactly one flagged `is_primary`**, replacing the referral's free-text `specialty`. | M | "One primary" makes routing and utilization-by-specialty deterministic. | Branch (14) |
| FR-BRN-025 | A practitioner shall be assignable to **one or many branches** via `practitioner_branch_assignment` with a validity window and status. | M | Doctors rotate between sites; a single-site model would not survive contact with the rota. | Branch (14) |
| FR-BRN-026 | The system shall **reject creation of availability, and reject booking, for a doctor at a branch they are not assigned to**, returning `422` with a clear, actionable reason — validated at *both* availability creation and booking time. | M | Catching it only at booking strands patients; catching it only at availability lets manual bookings through. | Branch (14) |
| FR-BRN-027 | The **doctor picker shall filter by active branch + specialty**, and the clinician profile shall display specialty. | M | Booking staff must not be able to pick a clinician who cannot serve the site. | Branch (14) |
| FR-BRN-028 | Specialty shall drive **referral routing**, **utilization-by-specialty reporting**, and **default examination-type suggestions**. | S | The structured field pays for itself across three workflows. | Branch (14) |
| FR-BRN-029 | **Licence expiry** shall feed the existing credential-reminder sweep, and an expired licence shall be surfaced on the practitioner profile and rota views. | S | Compliance and quality; reuses machinery already built. | Cross-cutting (14) |
| FR-BRN-030 | Branch shall be available as a **reporting dimension** (`branch_code`) across operational and financial reports, without becoming an access boundary for MemberScoped roles. | S | Managers steer by site; analysts must not be walled in by it. | Branch (14) / Reporting |

**16.5 Examination types & sensitivity classification (`SEN`)**

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-SEN-001 | The system shall maintain an **`examination_type`** master-data reference (code, EN/AR name, `category` ∈ {`Lab`,`Imaging`,`Procedure`,`Consultation`,`Assessment`}, default code system + code linking to CPT/LOINC, status). | M | Orders carried codes but no orderable catalogue to classify or govern. | Sensitivity (14) |
| FR-SEN-002 | Each examination type shall carry a **`sensitivity_level`** ∈ {`Standard`,`Sensitive`,`HighlySensitive`} and a **`sensitive_category`** ∈ {`MentalHealth`,`HIV_STI`,`Genetic`,`SubstanceUse`,`ReproductiveHealth`,`GBV_Forensic`,`Other`}. | M | Special-category clinical data must be identifiable *as data*, not by convention. | Sensitivity (14) |
| FR-SEN-003 | The sensitive-category list shall be **configuration, not code**, ratified by the Medical Director + DPO; **`MentalHealth` is the confirmed requirement** and the remainder are the standard special-category set under Egypt PDPL and UNHCR data-protection norms. | M | The policy owner, not the developer, decides what counts as special-category. | Cross-cutting (14) |
| FR-SEN-004 | The order and each order line shall carry the `examination_type_id` and a **denormalized `sensitivity_level` pinned at order creation**; gating shall never depend on a cross-service join at read time. | M | A gate that requires a remote lookup fails open under load — which is precisely the wrong failure. | Cross-cutting (14) |
| FR-SEN-005 | Results and report documents shall **inherit the classification** of the order line that produced them. | M | Classification must follow the data, not the request. | Sensitivity (14) |
| FR-SEN-006 | A later reclassification of an `examination_type` shall **not retroactively downgrade** the pinned sensitivity of existing orders/results; upgrades shall apply going forward and be audited. | M | Data classified as sensitive at capture stays sensitive; consent and expectation were set then. | Cross-cutting (14) |

**16.6 Sensitive result gating (`SEN`)**

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-SEN-007 | For a result whose `sensitivity_level` ≠ `Standard`, **full content** (values, report document, clinical narrative) shall be readable **only by the authoring/ordering doctor** with a treating relationship — and by the beneficiary themselves via a future portal. | M | Special-category data has a minimum-necessary audience of one. | Sensitivity (14) |
| FR-SEN-008 | **Every other principal** — other treating clinicians, the **medical approval team**, Medical Director, case managers, reception, finance/claims and reporting — shall receive **existence metadata only**: category, date, status, ordering branch and a `RESTRICTED` marker. **Never values, never the report.** | M | Existence is enough to coordinate care and adjudicate benefit; content is not. | Cross-cutting (14) |
| FR-SEN-009 | This gate shall **deliberately override the approval team's standing EMR read** ([FR-CLIN-013](#4-clinical--emr-clin), [FR-AUTH-003](#7-approvals--authorizations-auth)): authorization decisions on sensitive services shall proceed on **existence + the requesting doctor's clinical justification**, or via an approved release request. | M | The one non-treating role with broad clinical read is exactly the role this rule exists to bound. | Cross-cutting (14) |
| FR-SEN-010 | Sensitive result content shall be excluded from **exports, printed packets, read-model projections, search indexes and outbound notifications**; a notification shall say a restricted result is available, never what it says. | M | A gate on the read path that leaks through the report path is not a gate ([FR-NOT-003](#9-notifications-not)). | Cross-cutting (14) |
| FR-SEN-011 | The system shall emit **`SensitiveResultRestricted`** when a restricted result is created, so downstream consumers project the restricted form from the outset. | M | Read models must never materialize content they may not serve. | Cross-cutting (14) |

**16.7 Release request, decision & grants (`SEN`)**

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-SEN-012 | A principal needing content shall raise a **`report_access_request`** carrying the result reference, requester, requested-for role, a **mandatory `purpose_code`** ∈ {`ContinuityOfCare`,`AuthorizationDecision`,`ClinicalReview`,`Complaint`,`Legal`,`Other`}, a **mandatory free-text `justification`**, and a requested TTL. | M | Access to special-category data must be *asked for*, on the record, with a stated reason. | Sensitivity (14) |
| FR-SEN-013 | A request missing `purpose_code` or `justification` shall be **rejected at validation** (`422`), never accepted as a draft that could be approved. | M | An unjustified request must not be approvable by accident. | Sensitivity (14) |
| FR-SEN-014 | The request shall follow the lifecycle `Requested → UnderReview → (InfoRequested ⇄ UnderReview) → (Approved \| Denied)`, with `Approved → (Expired \| Revoked)`, routed on creation to the **authoring/ordering doctor** ([23-state-machines.md](23-state-machines.md)). | M | Canonical, testable release state machine. | Sensitivity (14) |
| FR-SEN-015 | A request shall be decidable by the **authoring/ordering doctor OR a Medical Director**; a Director decision shall be flagged **`decided_by_role = MedicalDirector`** and **extra-audited** as a high-severity event. | M | Care must not stall because one clinician is unavailable — but the shortcut must be visible. | Sensitivity (14) |
| FR-SEN-016 | The **requester shall never be the decider** (segregation of duties), and the decision screen shall show requester, role, purpose, justification and requested duration. | M | Self-release is not release. | Cross-cutting (14) |
| FR-SEN-017 | A **denial shall require a reason** and shall be notified to the requester. | M | A refusal without a reason cannot be appealed or improved on. | Sensitivity (14) |
| FR-SEN-018 | An approval shall produce a **`report_access_grant`** that is **time-boxed** (default **72 h** `Sensitive`, **24 h** `HighlySensitive`, configurable), **scoped to exactly one result**, and **non-transferable** — bound to a single `grantee_user_id`, never to a role, team or queue. | M | A durable, shareable grant is a permanent leak with extra steps. | Cross-cutting (14) |
| FR-SEN-019 | Grants shall **auto-expire**, and shall be **revocable** at any time by the authoring doctor, a Medical Director or the DPO; expiry and revocation shall be audited and notified. | M | Access must decay by default and be withdrawable on demand. | Cross-cutting (14) |
| FR-SEN-020 | **Every read performed under a grant shall be audited separately** from ordinary PHI-read audit, carrying `grant_id`, `purpose_code`, actor, result reference and correlation id, and shall emit `SensitiveResultReadUnderGrant`. | M | The grant register is the evidence that the exception stayed exceptional. | Cross-cutting (14) |
| FR-SEN-021 | For a `HighlySensitive` result, **Medical Director visibility of the release shall be mandatory** and the default grant TTL shall be shorter. | M | The highest-risk category carries the tightest default. | Sensitivity (14) |
| FR-SEN-022 | The system shall publish the release events via the **transactional outbox**: `ReportAccessRequested`, `ReportAccessInfoRequested`, `ReportAccessApproved`, `ReportAccessDenied`, `ReportAccessGrantExpired`, `ReportAccessGrantRevoked`, `SensitiveResultReadUnderGrant`. | M | Notification, reporting and audit without coupling. | Cross-cutting (14) |
| FR-SEN-023 | The system shall report **release KPIs** — request volume by purpose, approval/denial rate, decision TAT, Medical-Director-decided share, grants active/expired/revoked, and reads-under-grant. | S | If the exception path is growing, governance must see it. | Sensitivity (14) / Reporting |

**16.8 Break-glass, data-subject rights & UI (`SEN`)**

| ID | Statement | Priority | Rationale | Phase |
|----|-----------|----------|-----------|-------|
| FR-SEN-024 | **Break-glass shall remain available** on a sensitive result for genuine emergencies but shall be **loud**: extra justification, immediate notification to the **authoring doctor and the Medical Director and the DPO**, and **mandatory retrospective review** — reusing the existing `BreakGlass` machinery. | M | Emergencies happen; silent emergencies are how the gate erodes. | Cross-cutting (14) |
| FR-SEN-025 | The **beneficiary's own access** to their data shall be **unaffected** by sensitivity gating (data-subject rights, [20-compliance-checklist.md](20-compliance-checklist.md)). | M | The gate protects the subject; it must never be used against them. | Cross-cutting (14) |
| FR-SEN-026 | A restricted result shall render in a **locked state** — category + date + `RESTRICTED` chip using four cues (neutral hue + lock icon + ghost pill + text) — with a **"Request access"** action opening the justification form; screens shall be bilingual (Arabic RTL + English) and meet **WCAG 2.2 AA**. | M | Non-colour status encoding and full a11y/i18n are platform-wide requirements ([21-accessibility-checklist.md](21-accessibility-checklist.md), [0B](0B-DESIGN-SYSTEM-UI.md)). | Cross-cutting (14) |
| FR-SEN-027 | Appointment, queue and order screens shall **display the active branch** prominently, and the **branch switcher** shall be keyboard-operable, announce changes via `aria-live`, mirror under RTL, and be audited ([14-navigation-structure.md](14-navigation-structure.md)). | M | A user must never be able to mistake which site they are working in. | Cross-cutting (14) |

---

## 17. Priority summary (MoSCoW rollup)

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
| Claims Management (10b) | 65 | 3 | 0 |
| Branch Scoping & Practitioner Specialty (14) | 24 | 5 | 1 |
| Clinical Sensitivity (14) | 26 | 1 | 0 |

> The **Consumption Invariants** and **Data-Minimization** FRs are release-gating: no MVP is acceptable without them ([28-mvp-definition.md](28-mvp-definition.md)). The **`SEN` gating FRs** (FR-SEN-007 … FR-SEN-010, FR-SEN-018 … FR-SEN-020) and the **branch-denial FR** (FR-BRN-017) are release-gating for phase 14 in the same way.

---

### Cross-references
- Non-functional targets: [08-non-functional-requirements.md](08-non-functional-requirements.md)
- Screen realization: [12-ui-wireframes.md](12-ui-wireframes.md) · Flows: [13-ux-flows.md](13-ux-flows.md)
- Who can do each FR: [10-role-matrix.md](10-role-matrix.md) · [11-permission-matrix.md](11-permission-matrix.md)
- Lifecycles referenced: [23-state-machines.md](23-state-machines.md)
- Claims module design (phase 10b): [36-claims-management.md](36-claims-management.md)
- Branch scoping, practitioner specialty & clinical sensitivity design (phase 14): [37-branch-scoping-and-clinical-sensitivity.md](37-branch-scoping-and-clinical-sensitivity.md)
