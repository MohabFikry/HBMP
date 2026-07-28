# 10 — Role Matrix (Human-Readable Companion)

[⬅ Back to Index](00-README-INDEX.md) · [Design Foundations](0A-DESIGN-FOUNDATIONS.md)

**Siblings:** [11-permission-matrix.md](11-permission-matrix.md) · [18-security-model.md](18-security-model.md) · [19-audit-strategy.md](19-audit-strategy.md) · [20-compliance-checklist.md](20-compliance-checklist.md) · [37-branch-scoping-and-clinical-sensitivity.md](37-branch-scoping-and-clinical-sensitivity.md)

> **Purpose of this document.** This is the *human-readable* definition of every role on the Mersal Healthcare Benefit Management Platform (HBMP). It explains **who** each role is, **why** it exists, **what portal** it lives in, **what data scope** it may reach, and **how roles relate** to one another. The machine-enforceable, field-level rules live in the companion [Permission Matrix](11-permission-matrix.md); the enforcement mechanics live in the [Security Model](18-security-model.md). Where the two disagree, the Permission Matrix and the policy engine are authoritative — this document is the narrative.

---

## 1. Design principles for roles

Every role definition on HBMP is derived from four hard constraints established in [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md):

1. **Least Privilege** — a role receives only the permissions required to perform its documented job function, nothing more.
2. **Need-to-Know** — even where a role *could* technically read a data class, access is further narrowed by *context* (treating relationship, provider ownership, tenant, assignment). This is the ABAC layer.
3. **Data Minimization** — each screen renders the minimum-necessary fields. A role that needs a patient's identity to book an appointment does not thereby get their diagnoses.
4. **Segregation of Duties (SoD)** — no single human can both *originate* and *approve* the same sensitive transaction (e.g., raise a medical approval and approve it; create a payment and release it).

Roles are **coarse-grained identities**; fine-grained gating is done by **RBAC (role → permission)** combined with **ABAC (attributes → conditions)**. See [Section 8](#8-rbac--abac-how-a-role-becomes-a-decision).

---

## 2. Scope vocabulary

Each role is bound to a **scope** — the horizon of records it may ever touch, before per-field rules apply.

| Scope token | Meaning | Enforced by |
|---|---|---|
| `tenant:own` | Only records belonging to the beneficiary's/organization's own tenant (the Mersal Foundation tenant, or a partitioned sub-tenant). | PostgreSQL Row-Level Security (RLS) + gateway claim |
| `provider:own` | Only records owned by the provider organization the user belongs to (a specific clinic, lab, pharmacy, imaging center). | RLS `provider_id` predicate + ABAC `provider-ownership` |
| `beneficiary:assigned` | Only beneficiaries explicitly assigned/linked to this user (panel, case load, active encounter). | ABAC `treating-relationship` / `assignment` |
| `beneficiary:treating` | Only beneficiaries with an *active clinical encounter* with this user. | ABAC `treating-relationship` + `order-status` |
| `self` | Only the user's own account/profile records. | Identity claim `sub` |
| `global` | Cross-tenant, cross-provider (platform operations). Reserved for Super Admin / platform SRE, break-glass only for data. | Explicit elevated role + step-up MFA + audit |

**Scope mode** (the *branch* dimension — see [37-branch-scoping-and-clinical-sensitivity.md](37-branch-scoping-and-clinical-sensitivity.md)). Mersal operates **six internal branches** (`ASW` Aswan, `ALX` Alexandria, `OCT` 6th of October, `MAA` Maadi, `DOK` Dokki, `NSR` Nasr City), modeled as a `branch` entity **distinct from a contracted `provider_location`**. Every internal user has exactly one **Home** branch plus optional **Additional** branches; an **active-branch switcher** (header `X-Active-Branch`, defaulting to Home) sets the working context and is **validated server-side against the permitted set** — never trusted from the client.

| Scope mode | Meaning | Enforced by |
|---|---|---|
| `BranchScoped` | Worklists, queues, appointment/day lists, encounters and branch-originated orders are **filtered server-side to the active branch**. Other branches are **inaccessible**, not merely hidden — a cross-branch request is **denied (403 + audited `BranchScopeDenied`)**, not returned empty. | ABAC `BSC` (BranchScope) + `RowScope.BranchIds` + optional RLS predicate |
| `MemberScoped` | Work is **beneficiary/member-centred and spans all branches** by default. A branch filter is offered as a *convenience*, never as a restriction. | `RowScope.BranchUnrestricted` (policy bundle declares the mode) |
| `ProviderScoped` | Unchanged: scoped to the contracted provider's own queue. Mersal's branch dimension does not apply. | ABAC `PO` (provider-ownership) + RLS `provider_id` |

**Non-negotiable:** branch scoping is an **additional narrowing filter, never a replacement for an existing control.** A doctor still needs `TR` to open a record and still receives only min-necessary fields; branch scoping only narrows *which worklist* they see.

| Role | Scope mode |
|---|---|
| Reception | **BranchScoped** |
| Appointment Coordinator *(scheduling variant of Reception; see note)* | **BranchScoped** |
| Nurses | **BranchScoped** |
| Doctors | **BranchScoped** *(operational lists only — clinical record access stays `TR`-gated and cross-branch)* |
| Branch/Clinic Manager (§3.20) | **BranchScoped** *(one or more assigned branches)* |
| Beneficiary Management | MemberScoped |
| Call Center | MemberScoped *(all branches — central hotline)* |
| Call Centre Supervisor (§3.21) | MemberScoped *(all branches — same data exclusions as the agent)* |
| Medical Approval | MemberScoped |
| Medical Director | MemberScoped |
| Case Managers | MemberScoped |
| Finance | MemberScoped |
| Claims Officer / Claims Reviewer | MemberScoped |
| Network Team | MemberScoped |
| Policy Administrator (§3.22) | MemberScoped *(all branches — a benefit product is not a branch-local fact)* |
| Beneficiary-Management Supervisor (§3.23) | MemberScoped *(all branches — supervises officers wherever they sit)* |
| Org Admin / Super Admin | MemberScoped *(administrative; branch is a filter, not a boundary)* |
| Reporting/manager views | MemberScoped |
| Labs · Imaging Centers · Pharmacies · Provider Admin | ProviderScoped |

> **Appointment Coordinator** is not a separate portal identity: it is the scheduling-only duty set held by Reception (and by Call Center staff working a named site). Where it is granted as a distinct Keycloak group it is **BranchScoped** exactly like Reception.

**Sensitivity tiers** (data classes a role habitually handles; drives logging verbosity, MFA strength, and review cadence):

| Tier | Label | Examples of data | Typical control uplift |
|---|---|---|---|
| **T0** | Public/operational | Provider directory, benefit catalog names | Standard |
| **T1** | Restricted PII | Beneficiary name, contact, appointment slot | RLS + read audit |
| **T2** | Sensitive PII / financial | UNHCR/registration ID, claims, invoices, coverage | Field masking + read audit + SoD |
| **T3** | PHI / clinical | Diagnoses, EMR notes, prescriptions, lab & imaging results | Need-to-know ABAC + read-of-PHI audit + step-up |
| **T4** | Platform-critical | Keys, policies, role bindings, audit config | Break-glass, dual-control, hardware MFA |

---

## 3. Role catalog

Each role below follows the same template: **Purpose · Portal · Typical users · Scope · Scope mode · Key capabilities · Highest sensitivity tier · Notes**.

### 3.1 Beneficiary Management
- **Purpose:** Own the beneficiary lifecycle — intake, registration, identity verification, household linkage, eligibility enrollment, benefit assignment, and record maintenance for refugee beneficiaries.
- **Portal:** Beneficiary Management portal.
- **Typical users:** Mersal enrollment officers, registration desk supervisors, data-quality staff.
- **Scope:** `tenant:own` over all beneficiaries; write to demographic + eligibility + policy-link records.
- **Scope mode:** **MemberScoped** — registration is member-centred and spans all branches; the registering branch is recorded on the record but never restricts the worklist.
- **Key capabilities:** Create/update beneficiary demographic records; verify identity documents; link to UNHCR/refugee registration references; assign benefit plans (with Policy service); manage household/dependents; deactivate/merge duplicates; initiate data-subject-request workflows.
- **Highest tier:** **T2** (registration IDs, refugee status). **No routine clinical (T3) access.**
- **Notes:** May *see that* a beneficiary has an active plan and eligibility status, but **not** clinical diagnoses. Merges and deactivations are dual-controlled (SoD with a supervisor).

### 3.2 Reception
- **Purpose:** Front-desk check-in, appointment scheduling, queue management, and identity confirmation at point of service.
- **Portal:** Reception portal.
- **Typical users:** Clinic front-desk staff at Mersal and partner sites.
- **Scope:** `provider:own` + `beneficiary:assigned` (those with an appointment/queue entry at this site today).
- **Scope mode:** **BranchScoped** — the queue, day-list and appointment book return **only the active branch**; a request for another branch is denied (403), not returned empty. The active branch defaults to the user's Home branch and may be switched only to a permitted (Home ∪ Additional) branch.
- **Key capabilities:** Search a beneficiary by ID/name; confirm identity + eligibility (green/red light only); book/reschedule/cancel appointments; manage waiting-room queue; print visit tickets; capture arrival.
- **Highest tier:** **T1** (name, contact, appointment). Sees an **eligibility verdict**, not the underlying policy math or clinical reason.
- **Notes:** **HARD RULE — Reception CANNOT view the EMR.** No diagnoses, notes, prescriptions, labs, or imaging. This is enforced at gateway, service, row and field level (see [Permission Matrix §3](11-permission-matrix.md)).

### 3.3 Call Center
- **Purpose:** Remote beneficiary support — inbound/outbound calls, appointment help, benefit questions, complaint intake, triage routing.
- **Portal:** Call Center portal (with CTI/soft-phone) — the call-shaped agent workspace (open call → search → verify → 360 → act → wrap up).
- **Typical users:** Mersal call-center agents and team leads.
- **Scope:** `tenant:own` for identity + eligibility + appointment + case-ticket data, plus the `call_interaction` / `caller_verification` records of their **own** calls.
- **Scope mode:** **MemberScoped (all branches — central hotline)** — the call centre is a *central hotline*, not a site desk: a caller may belong to any branch, and the agent searches, views and books **across all six branches**. **Branch and specialty are selectors on the booking form, never restrictions**; the policy bundle declares `RowScope.BranchUnrestricted` and the portal shows an **"All branches" indicator** instead of a branch switcher ([37 §3](37-branch-scoping-and-clinical-sensitivity.md)). Where an agent is dedicated to a single site, the Appointment-Coordinator duty set applies instead and is BranchScoped.
- **Key capabilities:** Open and close a **call interaction** (reason code, outcome, notes) correlated by `call_ref`; **verify caller identity before any disclosure** (see the hard rule below); search a member by **phone — the primary call-centre entry point** — or member no./national ID/passport/refugee ID/UNHCR no.; view the minimum-necessary **member 360** — eligibility verdict + coverage summary with **remaining limits**, contact details, **appointments across all branches** (type, time, branch, doctor name + specialty), open referrals and follow-ups due; **book / reschedule / cancel** appointments into any branch through the existing appointment engine, with a **mandatory cancel reason code**; update/correct contact details and preferred channel; open and track support tickets/cases; escalate to Case Managers.
- **Highest tier:** **T2** (coverage, financial limits summary). **No clinical detail** beyond appointment type, branch, doctor name and specialty.
- **Notes:** Every caller verification and every PII read is audited with a correlation ID tied to the call (`call_ref`). Agents see coverage **balances**, not **diagnoses**.
  **HARD RULE — verify before you disclose.** No member detail is shown until the agent records a **passed caller verification confirming at least TWO identifier types** (e.g. member no. + date of birth) for **this interaction and this beneficiary**. Pre-verification an agent sees only *match / no match*, the display name, and **which identifier types to challenge on** — never appointments, contacts, coverage or history. **Failed** verifications are persisted and audited, never silently discarded; a verification is **single-interaction, single-beneficiary, and expires when the call closes**. **Only identifier *types* are ever stored** in the call-centre records — never the values the caller recited (those stay in patient-service) — and the UI never displays a stored identifier value for the agent to read out: the caller states it, the agent confirms it.
  **HARD RULE — the Call Center sees no clinical data whatsoever.** No diagnoses, no EMR/clinical notes, no lab or imaging results, no prescriptions, no examination detail — the whole clinical field set is stripped server-side, exactly as for Reception ([Permission Matrix §3.2/§4](11-permission-matrix.md)). The only clinically-adjacent facts an agent may see are **that an appointment exists** and its **type, time, branch, doctor name and specialty**.

### 3.4 Doctors
- **Purpose:** Deliver clinical care — consult, diagnose, document encounters, order investigations, prescribe, and refer.
- **Portal:** Doctors (clinical) portal.
- **Typical users:** Physicians and specialists at Mersal and contracted providers.
- **Scope:** `beneficiary:treating` — **only patients they are actively treating** (active encounter/assignment).
- **Scope mode:** **BranchScoped for operational lists** (today's clinic list, waiting queue, branch-originated orders) — filtered to the active branch, which must be one the practitioner is assigned to. The **clinical record itself is not branch-partitioned**: once `TR` holds, the doctor sees the patient's authorized longitudinal record regardless of which branch generated it.
- **Key capabilities:** Full read of the treated patient's EMR (history, notes, results, prescriptions); create clinical notes + diagnoses; place orders (labs, imaging, procedures); write prescriptions; raise medical approval requests; create referrals; view results returned to their orders.
- **Highest tier:** **T3** (full PHI, scoped).
- **Notes:** **HARD RULE — Doctors view only patients they treat.** The treating-relationship is the ABAC gate; when the encounter closes and the retention window lapses, standing access narrows to continuity-of-care rules. Break-glass exists for emergencies (see [Security Model §11](18-security-model.md)). Doctors carry a **practitioner** profile with structured **specialty** (≥1, exactly one flagged primary) and one-or-many **branch assignments**; availability and booking are rejected (`422`) at a branch the doctor is not assigned to, and doctor pickers filter by **active branch + specialty**. **Sensitive results:** the **authoring/ordering doctor** is the only routine reader of a result whose `sensitivity_level` ≠ `Standard`, and is the **primary decider** on a release request ([37 §6](37-branch-scoping-and-clinical-sensitivity.md)).

### 3.5 Nurses
- **Purpose:** Support clinical delivery — vitals, triage, medication administration, care-plan tasks, and encounter prep.
- **Portal:** Nurses portal (a constrained clinical view).
- **Typical users:** Registered nurses, triage nurses, ward staff.
- **Scope:** `beneficiary:treating` / `beneficiary:assigned` (assigned encounter or ward panel).
- **Scope mode:** **BranchScoped** — triage lists, vitals queues and care tasks return only the active branch. Nurses also carry a `practitioner` profile (`practitioner_type = Nurse`) with branch assignments.
- **Key capabilities:** Record vitals and triage; view relevant clinical context for assigned patients (problem list, allergies, active meds, care tasks); administer/record medications; update nursing notes; see orders relevant to care delivery.
- **Highest tier:** **T3** (PHI, scoped and narrower than Doctors — e.g., limited authorship of formal diagnoses).
- **Notes:** Nurses **cannot author formal diagnoses** or issue prescriptions; they document observations and administration. Access is bounded to the assignment window.

### 3.6 Labs
- **Purpose:** Fulfill laboratory investigation orders and return results.
- **Portal:** Labs portal.
- **Typical users:** Lab technicians, pathologists at contracted laboratories.
- **Scope:** `provider:own` + `order-status`: only **lab orders routed to this lab**.
- **Scope mode:** **ProviderScoped** — unchanged by branch scoping; a contracted lab sees its own queue and never a Mersal branch worklist.
- **Key capabilities:** View the *order* (test requested, specimen, ordering clinician, minimum clinical indication needed to run the test safely); accept/reject/collect specimen; enter and verify results; attach reports; mark order complete.
- **Highest tier:** **T3** (their own investigation results).
- **Notes:** **HARD RULE — Labs CANNOT view prescriptions.** They see the clinical *indication* attached to their order (need-to-know), never the medication list or unrelated EMR history.

### 3.7 Imaging Centers
- **Purpose:** Fulfill radiology/imaging orders and return studies + reports.
- **Portal:** Imaging portal (may integrate PACS).
- **Typical users:** Radiographers, radiologists at contracted imaging centers.
- **Scope:** `provider:own` + `order-status`: only **imaging orders routed here**.
- **Scope mode:** **ProviderScoped** — unchanged by branch scoping.
- **Key capabilities:** View imaging order + indication; schedule study; upload images/DICOM references (MinIO with SSE); author radiology report; mark complete.
- **Highest tier:** **T3** (their own imaging studies/reports).
- **Notes:** Same minimization posture as Labs — indication yes, prescriptions/unrelated history no.

### 3.8 Pharmacies
- **Purpose:** Dispense prescribed medications and record dispensation/consumption against benefit.
- **Portal:** Pharmacies portal.
- **Typical users:** Pharmacists, dispensing techs at contracted pharmacies.
- **Scope:** `provider:own` + `order-status`: only **prescriptions routed to this pharmacy**.
- **Scope mode:** **ProviderScoped** — unchanged by branch scoping.
- **Key capabilities:** View the prescription (drug, dose, quantity, prescriber, essential safety context — allergies, interactions flags); verify eligibility/coverage for the drug; dispense and record consumption; partial fills; substitutions per policy.
- **Highest tier:** **T3** (prescriptions routed to them).
- **Notes:** **HARD RULE — Pharmacies CANNOT view investigation (lab/imaging) results.** They receive safety-relevant flags (e.g., renal-dose alert) as *derived* attributes, not raw results.

### 3.9 Medical Approval
- **Purpose:** Clinical utilization review — adjudicate prior-authorization / medical-approval requests against clinical evidence and benefit rules.
- **Portal:** Medical Approval portal.
- **Typical users:** Approval clinicians/nurses, utilization-review officers.
- **Scope:** `tenant:own` over **approval cases** and the clinical evidence attached to them.
- **Scope mode:** **MemberScoped (all branches)** — utilization review follows the member, never a site. A branch filter is a convenience only.
- **Key capabilities:** Review approval requests; **read EMR, clinical notes, and reports** relevant to the case; request additional info; approve/deny/pend with clinical rationale; apply benefit policy; set validity/limits.
- **Highest tier:** **T3** (broad clinical read — by design).
- **Notes:** **HARD RULE — Approval team CAN view EMR / clinical notes / reports.** This is the *only* non-treating role with broad clinical read, justified by the utilization-review purpose; it is offset by heavy read-of-PHI auditing and purpose binding. **SoD:** an approver cannot adjudicate a request they authored as a treating clinician.
  **Sensitivity carve-out (Phase 14) — HARD RULE:** this standing clinical read **stops at sensitive results.** For a result whose `sensitivity_level` ≠ `Standard`, the approval team sees **existence metadata only** (category, date, status, ordering branch, `RESTRICTED` marker) — never values, report body or narrative — unless an **active, single-result, non-transferable grant** exists. Authorization decisions on sensitive services proceed on existence + the requesting doctor's clinical justification, or via an approved release request ([37 §6](37-branch-scoping-and-clinical-sensitivity.md)).

### 3.10 Medical Director
- **Purpose:** Clinical governance — oversight of approvals, escalations, appeals, quality, and clinical policy.
- **Portal:** Medical Director portal (superset of Approval + oversight dashboards).
- **Typical users:** Chief medical officer, senior clinical leads.
- **Scope:** `tenant:own` over approvals, escalations, clinical KPIs; case-level clinical read for escalated/appealed cases.
- **Scope mode:** **MemberScoped (all branches)** — governance spans the whole network of branches; branch is a reporting dimension, not a boundary.
- **Key capabilities:** Handle escalations and appeals; override/uphold approval decisions (dual-control logged); define clinical review criteria; view aggregate + case-level clinical quality metrics; **decide sensitive-report release requests** where the authoring doctor is unavailable.
- **Highest tier:** **T3**.
- **Notes:** Overrides are always logged as *distinct* events with rationale; the Director cannot also have been the originating clinician (SoD).
  **New authority (Phase 14) — sensitive-report release.** The Director is the **alternate decider** on a `report_access_request` so that care is never blocked when the authoring/ordering doctor is unavailable. Such a decision is flagged `decided_by_role = MedicalDirector` and is **extra-audited** (over and above the ordinary decision audit); for `HighlySensitive` results Director visibility on release is mandatory and the grant TTL is shorter. The Director may also **revoke** a grant, and is notified — with the authoring doctor and the DPO — on every break-glass read of a sensitive result. The authority is to *decide release*, **not** a standing read: absent a grant the Director sees the same existence-only metadata as the approval team ([37 §6](37-branch-scoping-and-clinical-sensitivity.md)).

### 3.11 Case Managers
- **Purpose:** Coordinate care and benefits for a defined case load of beneficiaries (complex/chronic/vulnerable refugees).
- **Portal:** Case Managers portal.
- **Typical users:** Care coordinators, social workers, complex-case managers.
- **Scope:** `beneficiary:assigned` — their **assigned case load**.
- **Scope mode:** **MemberScoped (all branches)** — a case load follows people, who move between branches. Branch is a filter, never a restriction. **Sensitive results are existence-only to Case Managers**, exactly as for the approval team.
- **Key capabilities:** Holistic view of assigned beneficiaries (eligibility, care plan, key clinical summary needed for coordination, open approvals, appointments); coordinate referrals; open/track cases; liaise with Approval and providers; manage care plans.
- **Highest tier:** **T3** (scoped to case load; summary-level clinical for coordination).
- **Notes:** Access follows assignment; unassignment revokes it. Case Managers see coordination-relevant clinical summaries, not necessarily every raw result unless the care plan requires it.

### 3.12 Finance
- **Purpose:** Claims, invoicing, provider payments, reconciliation, benefit-cost accounting.
- **Portal:** Finance portal.
- **Typical users:** Finance officers, claims processors, accounts-payable staff.
- **Scope:** `tenant:own` over financial + claim + coverage records.
- **Scope mode:** **MemberScoped (all branches)** — finance reports across every branch; branch is a grouping dimension on utilization and settlement.
- **Key capabilities:** Process claims; validate against eligibility/coverage; generate invoices; schedule/approve provider payments (SoD split: initiate vs. release); reconcile; produce financial reports.
- **Highest tier:** **T2** (financial + coverage).
- **Notes:** **HARD RULE — Finance CANNOT view diagnoses.** Claims carry *coded, minimized* service/procedure references and amounts, but the **diagnosis field is masked/withheld** from Finance. Where a procedure code implies clinical detail, only the billing code needed for adjudication is exposed. Payment initiation and release are separate permissions held by different people. **As of Phase 10b**, claim *adjudication* is the dedicated **Claims Officer** role (§3.17); Finance consumes the resulting settlement advice and executes payment outside the platform ([36-claims-management.md](36-claims-management.md)).

### 3.13 Provider Admin
- **Purpose:** Administer a single provider organization's users, sites, schedules, and service catalog on the platform.
- **Portal:** Provider Admin portal.
- **Typical users:** Clinic/lab/pharmacy administrators (provider-side).
- **Scope:** `provider:own` — their own provider org only.
- **Scope mode:** **ProviderScoped** — a contracted org's own sites; Mersal `branch` rows are not visible to and not administrable by a Provider Admin.
- **Key capabilities:** Manage provider-side user accounts and role assignments (within an allowed set); configure sites, rooms, schedules; maintain service/price catalog entries subject to Network approval; view provider-scoped operational reports.
- **Highest tier:** **T2** (provider operational + some financial). **No clinical (T3) read** into beneficiary EMR.
- **Notes:** Strong **provider isolation** — a Provider Admin can never see another provider's users or data. Cannot grant clinical roles beyond the platform-sanctioned catalog; cannot self-elevate.

### 3.14 Network Team
- **Purpose:** Manage the provider network — onboarding, contracting, credentialing, catalog/pricing governance, performance.
- **Portal:** Network Team portal.
- **Typical users:** Mersal network/contracting managers.
- **Scope:** `tenant:own` across **provider organizations** (contract/credential/catalog metadata), **not** beneficiary clinical data.
- **Scope mode:** **MemberScoped (all branches)** — and, with Org Admin, the maintainer of **`branch` reference data, practitioner records/specialties and branch assignments** (see §7 guardrails).
- **Key capabilities:** Onboard/offboard providers; manage contracts, rates, credentialing; approve provider catalogs/prices; monitor network performance and compliance.
- **Highest tier:** **T2** (provider contracts/financial terms).
- **Notes:** Operates on provider metadata, not beneficiary PHI. Rate/catalog changes are SoD-controlled with Finance/Medical Director where clinical-cost policy is affected.

### 3.22 Policy Administrator  *(phase 19)*
- **Purpose:** Author the benefit PRODUCT — payers, plans, effective-dated plan versions, benefit rules and per-tier cost-share — and the policies written against it.
- **Portal:** Policy administration portal (`/policy/*`).
- **Typical users:** Mersal benefit/product managers.
- **Scope:** `tenant:own` across payers, plans, plan versions, policies, member groups and enrolments; **payer-restricted** where an assignment exists (a user assigned to specific payers sees only those payers' policies — see ADR-0024).
- **Scope mode:** **MemberScoped (all branches).**
- **Key capabilities:** Create/amend/activate plan versions (Draft-only editing, ADR-0017); issue, amend and renew policies; attach plans to a policy; read the membership book and its utilization; cancel another user's note and approve a retro-effective change (`policy:supervise`); read network tiers.
- **Highest tier:** **T2** (entitlement and money).
- **Notes:** **HARD RULE — no clinical route exists in this portal at all.** Policy administration reads entitlement and money, never a diagnosis; `Clinical`/`Restricted` note bodies and document content are withheld by CLASS (ADR-0018/0021), not by a role list. **Network tiers are READ-ONLY here:** this role prices benefits *at* a tier while the Network Team decides *which* tier a provider sits in (ADR-0019) — granting both would let one role reprice the network by moving a provider. Holds `reporting:read` for the 19.6b dashboard's operational views but **not** `reporting:read-financial`: cost-per-member and net-payable stay with Finance.

### 3.23 Beneficiary-Management Supervisor  *(phase 19)*
- **Purpose:** The supervisory increment over member administration — the two acts that exist for a second pair of eyes.
- **Portal:** Beneficiary Management portal (same portal as the officer; the supervisory affordances appear).
- **Typical users:** Beneficiary-management team leads.
- **Scope:** everything the Beneficiary Management officer holds, plus `policy:supervise`.
- **Scope mode:** **MemberScoped (all branches).**
- **Key capabilities:** Everything at §3.4, plus cancelling **another user's** note (with a mandatory reason, ADR-0018) and approving a **retro-effective** enrolment change.
- **Highest tier:** **T2.**
- **Notes:** Deliberately a separate role rather than a flag on the officer role — a supervisory power every officer holds is not a supervisory power. Listed in full in the identity seed rather than inherited, because role inheritance is invisible at the point of audit and "why could this person cancel that note" must be answerable from one row.

### 3.15 Org Admin
- **Purpose:** Administer the Mersal (tenant) organization — internal users, role assignments, org-level configuration, and policy within one tenant.
- **Portal:** Org Admin portal.
- **Typical users:** Mersal IT/operations administrators.
- **Scope:** `tenant:own` — administrative metadata (users, roles, org settings). **Not** a data-reader of PHI.
- **Scope mode:** **MemberScoped** — administers users across all branches and owns **`user_branch_assignment`** (Home/Additional). Granting a branch is an *administrative* act and confers no data read (see §7 guardrails).
- **Key capabilities:** Manage internal user accounts, group/role assignments (within tenant), MFA/device/IP policy at tenant level, org configuration, service-desk of access requests.
- **Highest tier:** **T4** for *administrative* objects (role bindings, policy) — **but not** clinical/financial *content*.
- **Notes:** Org Admin manages *who can access*, not *the data itself*. Assigning a clinical role does not grant Org Admin clinical read. All admin actions are audited and (for sensitive grants) dual-controlled.

### 3.16 Super Admin
- **Purpose:** Platform-wide technical administration and last-resort operations across all tenants/providers.
- **Portal:** Super Admin / platform console.
- **Typical users:** A very small number of Mersal platform engineers/SRE.
- **Scope:** `global` for **configuration and platform health**; **data access only via break-glass** with dual-control + step-up + full audit.
- **Scope mode:** **MemberScoped** for configuration surfaces (all branches). Break-glass on a **sensitive** result is *loud*: it notifies the authoring doctor, the Medical Director and the DPO, and forces retrospective review ([37 §6.2](37-branch-scoping-and-clinical-sensitivity.md)).
- **Key capabilities:** Manage tenants and platform config; deploy policy bundles; manage keys lifecycle (with OpenBao/Vault RBAC, not raw key exposure); operate infrastructure; invoke and review break-glass.
- **Highest tier:** **T4**.
- **Notes:** Super Admin is **not** a routine data reader. Any access to beneficiary PHI/financials requires an explicit, time-boxed break-glass grant that is loudly audited and reviewed. Hardware-backed MFA, IP allowlist, and PIM-style just-in-time elevation required. See [Security Model §11](18-security-model.md).

### 3.17 Claims Officer *(new — Phase 10b)*
- **Purpose:** Adjudicate delivered, authorized services into decided and settleable financial records — review claim lines, decide them, assemble and manage batches, record adjustments, and generate settlement advice. The claims counterpart to Medical Approval: same discipline, **money instead of medicine**.
- **Portal:** Claims portal (a distinct workspace alongside Finance; see [36-claims-management.md](36-claims-management.md)).
- **Typical users:** Mersal claims processors/adjudicators, reimbursement assessors.
- **Scope:** `tenant:own` over `claim`, `claim_line`, `claim_batch`, `claim_adjustment`, `reimbursement_request`, `claim_document`, `settlement_advice`. **Explicitly *not* `provider:own`** — a Claims Officer must not be affiliated with any provider whose claims they decide.
- **Scope mode:** **MemberScoped (all branches)** — claims are adjudicated across the whole network; `branch` is a batching/reporting dimension (alongside `provider_location`), never an access boundary.
- **Key capabilities:** Work the adjudication worklist (system recommendation + coded reasons); record **line-level** decisions — approve / partially approve / deny with mandatory coded reason / adjust / request info / route to clinical review; manually price `NO_TARIFF` lines; create and manage batches (date range, provider branch, provider group, manual selection) and roll line decisions up to batch totals; run reconciliation (billed-not-delivered, delivered-not-billed, price/quantity variance, duplicate); record append-only adjustments; assess beneficiary reimbursement requests including confirming OCR-extracted values; generate and export settlement advice.
- **Highest tier:** **T2** (financial + coded service references + coverage). **No clinical (T3) access.**
- **Notes:** **HARD RULE — Claims Officer CANNOT view diagnoses, EMR notes, or lab/imaging result *values*.** They adjudicate on **service codes, quantities, dates, amounts, authorizations and documents**. Result/report **existence, date and document reference** *are* visible as proof-of-service — the clinical **content** is stripped server-side from every claims projection ([Permission Matrix §3.2/§4](11-permission-matrix.md)). The platform **never executes payment**: settlement advice is a hand-off artifact to Finance/treasury. **SoD:** cannot decide a claim they originated/submitted, and cannot decide a claim belonging to a provider they are affiliated with.

### 3.18 Claims Reviewer (Senior) *(new — Phase 10b)*
- **Purpose:** Dual-control approver and escalation point for claims — the second pair of eyes required before an override above threshold, a high-value adjustment, or an exceptional settlement is committed.
- **Portal:** Claims portal (senior view: dual-control queue, override/adjustment approvals, batch oversight, claims KPIs).
- **Typical users:** Claims team leads, senior adjudicators, claims managers.
- **Scope:** `tenant:own` — same claims object set as the Claims Officer, plus the dual-control approval queue and batch-level exception actions.
- **Scope mode:** **MemberScoped (all branches)** — identical to the Claims Officer.
- **Key capabilities:** Everything a Claims Officer can do, plus: approve/reject **overrides above the configured value threshold**; approve **high-value adjustments** (large deductions, write-offs, recoveries/clawbacks, a negative net-payable batch); authorise removal of a claim from an `UnderReview` batch; uphold or remand appeals before re-adjudication; monitor claims TAT, denial-reason mix, and provider variance.
- **Highest tier:** **T2**. **Same clinical exclusions as the Claims Officer — seniority grants no clinical read.**
- **Notes:** **SoD is strict:** the Reviewer approving an override or adjustment must **not** be the officer who recorded it, must not be the claim's originator/submitter, and must not be provider-affiliated for that claim. Dual-control approvals are logged as *distinct* events with rationale ([Audit Strategy](19-audit-strategy.md)).

### 3.19 Clinical reviewer hand-off (no new role)

Some claim lines turn on a genuine **medical-necessity** question that cannot be settled from codes and amounts alone. These lines are **routed to `ClinicalReview`** and land with the **existing [Medical Approval](#39-medical-approval) / [Medical Director](#310-medical-director)** roles — not with a new claims-clinical hybrid.

| Aspect | Claims Officer / Reviewer | Clinical Reviewer (Medical Approval / Director) |
|---|---|---|
| Sees clinical context (diagnosis, notes, result values) | **Never** | **Yes**, under `PUR` purpose-binding, for the routed line only |
| Records | The **payment decision** (approve/partial/deny/adjust) | A **clinical opinion** on medical necessity |
| Makes the payment decision | **Yes** | **No** |

The hand-off is one-directional in each phase: the clinical opinion returns as a structured verdict + rationale that the Claims Officer acts on; the officer never gains clinical read by virtue of the routing, and the clinical reviewer never gains claims-decision rights. Both hops are audited.

### 3.20 Branch / Clinic Manager *(new — Phase 14)*
- **Purpose:** Operational oversight of **one or more Mersal branches** — staffing and rota coverage, schedule and availability health, queue throughput and waiting times, no-show and utilization patterns, and day-to-day site administration for the branches they are assigned to.
- **Portal:** Branch Operations view (an operational dashboard layered on the Reception/scheduling surfaces; see [14-navigation-structure.md](14-navigation-structure.md)).
- **Typical users:** Branch managers and clinic managers at Aswan, Alexandria, 6th of October, Maadi, Dokki, Nasr City.
- **Scope:** `tenant:own` over **operational** objects of their assigned branches — appointments, slots/availability, queue entries, encounter *status*, branch-originated order *status*, practitioner branch assignments and rota. **No routine clinical (T3) read.**
- **Scope mode:** **BranchScoped** — every list is filtered server-side to the **active branch**, which must be one of their permitted (Home ∪ Additional) branches. A manager of several branches switches between them; a request for an unassigned branch is **denied (403 + audited)**, not returned empty.
- **Key capabilities:** View and manage the branch day-list, queue and appointment book; monitor waiting times, throughput, no-shows and slot utilization for the active branch; maintain branch opening hours and availability templates; view practitioner coverage (specialty × session) and request assignment changes; view branch-level operational reports.
- **Highest tier:** **T1–T2** (identity + appointment + operational metrics). **No EMR, no diagnoses, no result values.**
- **Notes:** **HARD RULE — a Branch Manager is an operations role, not a clinical one:** the clinical field set is stripped exactly as for Reception, and a **sensitive** result is never visible beyond existence metadata. **A Branch Manager cannot grant themselves (or anyone) a branch assignment** — `user_branch_assignment` changes are made by **Org Admin** (staff) / **Network Team** (practitioner records), on request, and are audited (§7). Branch-level reporting is available across branches only in aggregate, via a MemberScoped reporting grant.

### 3.21 Call Centre Supervisor *(new — Phase 15)*
- **Purpose:** Supervise the contact-centre team — oversee call activity and quality, coach agents, review verification failures and complaint outcomes, and own the call-centre KPIs. The senior variant of [Call Center](#33-call-center), **not** a wider data role.
- **Portal:** Call Center portal (supervisor view: team call history, KPI board, escalations) — the agent workspace plus team-level lists.
- **Typical users:** Call-centre team leads, shift supervisors, quality/coaching staff.
- **Scope:** `tenant:own` over the **team's** `call_interaction` + `caller_verification` records (an agent sees only their own); identical member surfaces to an agent when personally handling a call.
- **Scope mode:** **MemberScoped (all branches — central hotline)** — identical to the agent; branch is a reporting dimension only.
- **Key capabilities:** Everything a Call Center agent can do, plus: read the **whole team's** call history and interaction detail (reason code, outcome, notes, linked appointment changes); monitor call-centre KPIs — calls handled per agent/day, average handle time, first-contact resolution, reason-code mix, appointments booked/rescheduled/cancelled via the call centre, **verification-failure rate**, abandoned rate; review verification failures and complaint escalations; reassign or escalate follow-ups.
- **Highest tier:** **T2** (coverage/limits summary + operational metrics). **Same clinical exclusions as the agent — seniority grants no clinical read.**
- **Notes:** **HARD RULE — supervision widens *whose* calls are visible, never *what fields* are visible.** A supervisor sees more interactions, not more data: **no diagnoses, EMR notes, results, prescriptions or examination detail**, and **no identifier values** (call records store only which identifier *types* were confirmed). KPI and coaching views are **aggregate and PHI-free**. **Verify-before-disclose still binds the supervisor** whenever they handle a call themselves — reviewing a colleague's call log is never a substitute for verifying the caller in front of them.

---

## 4. Role hierarchy & inheritance

HBMP uses **shallow, explicit inheritance** — deep inheritance trees hide privilege. Where inheritance exists it is documented and additive only within the same data domain.

```mermaid
graph TD
    subgraph Clinical
        NUR[Nurses] -->|subset of| DOC[Doctors]
        DOC --> APPR[Medical Approval]
        APPR --> MD[Medical Director]
    end
    subgraph Coordination
        CM[Case Managers]
    end
    subgraph Front-office
        REC[Reception]
        CC[Call Center] --> CCS[Call Centre Supervisor]
        BM[Beneficiary Management]
        BRM[Branch/Clinic Manager]
    end
    subgraph Provider-side
        PADM[Provider Admin]
        LAB[Labs]
        IMG[Imaging Centers]
        PH[Pharmacies]
    end
    subgraph Admin
        ORG[Org Admin] --> SUPER[Super Admin]
        NET[Network Team]
        FIN[Finance]
    end
    subgraph Claims
        CLMO[Claims Officer] --> CLMR[Claims Reviewer Senior]
    end
    CLMO -.->|routes medical-necessity<br/>lines, gains no clinical read| APPR
```

**Reading the diagram:** an arrow `A --> B` means B's *clinical-read* capability is a **superset** of A's within the same domain — it does **not** mean B inherits A's write duties or portal. Inheritance is used only to reason about read scope; every permission is still enumerated explicitly in the [Permission Matrix](11-permission-matrix.md). Non-connected roles are peers with disjoint duties.

| Inheritance edge | What is inherited | What is NOT inherited |
|---|---|---|
| Nurses → Doctors | Clinical *read* context for treated patients | Diagnosis authorship, prescribing |
| Doctors → Medical Approval | Ability to *read* clinical evidence | Treating-write; approval is read+adjudicate only |
| Medical Approval → Medical Director | Case clinical read | Director-only override/appeal powers add on top |
| Org Admin → Super Admin | Tenant admin surface | Cross-tenant + break-glass require explicit elevation |
| Claims Officer → Claims Reviewer (Senior) | Claims/batch read + line-decision surface | **No clinical read is added**; Reviewer-only dual-control approval of overrides/high-value adjustments adds on top |
| Call Center → Call Centre Supervisor | Visibility of the **team's** call interactions + call-centre KPIs | **No clinical read is added** and no identifier values; the member field set is identical to the agent's, and verify-before-disclose still applies |

The dashed edge `Claims Officer ⇢ Medical Approval` is **not** inheritance — it is a **hand-off** (§3.19). Routing a line to clinical review transfers the *question*, never the Claims Officer's field visibility.

---

## 5. Roles × Modules capability overview

Legend: **F** = Full (CRUD within scope) · **W** = Write/contribute · **R** = Read (scoped) · **R°** = Read minimized/verdict-only · **–** = No access · **A** = Approve/adjudicate · **C** = Consume/record fulfillment. All access is **further** constrained by scope + ABAC; this table is the coarse view. Modules map to microservices in [0A](0A-DESIGN-FOUNDATIONS.md).

| Role \ Module | patient | policy/elig. | emr | orders | approvals | provider | finance/claims | reporting | identity/admin | audit |
|---|---|---|---|---|---|---|---|---|---|---|
| Beneficiary Mgmt | F | W | – | – | – | R° | – | R° | R°(self dir.) | – |
| Reception | R° | R°(verdict) | – | R°(appt) | – | R°(own site) | – | – | – | – |
| Call Center | R/W(contact) | R°(coverage) | **–** | R/W(appt) | R°(status) | R° | R°(balance) | – | – | – |
| Call Centre Supervisor | R/W(contact) | R°(coverage) | **–** | R/W(appt) | R°(status) | R° | R°(balance) | R°(call-centre KPIs) | – | – |
| Doctors | R(treating) | R°(elig) | F(treating) | F(own) | W(raise) | R° | – | R°(own) | – | – |
| Nurses | R(assigned) | R° | W(assigned) | R(care) | – | R° | – | – | – | – |
| Labs | R°(order) | – | R°(indication) | C(lab) | – | R°(own) | – | R°(own) | – | – |
| Imaging | R°(order) | – | R°(indication) | C(imaging) | – | R°(own) | – | R°(own) | – | – |
| Pharmacies | R°(order) | R°(drug cov.) | R°(rx+safety) | C(rx) | – | R°(own) | R°(dispense claim) | R°(own) | – | – |
| Medical Approval | R | R(policy) | R(clinical) | R | A | R° | – | R° | – | – |
| Medical Director | R | R | R | R | A/override | R° | R°(cost) | R | – | – |
| Case Managers | R(assigned) | R(assigned) | R°(summary) | R(assigned) | R/W(request) | R° | R°(assigned) | R° | – | – |
| Branch/Clinic Manager | R°(appt identity) | – | **–** | R°(status, own branch) | – | R°(branch ops) | – | R°(branch ops) | R°(rota view) | – |
| Finance | R°(PII) | R(coverage) | **–** | R°(billing code) | R°(status) | R°(rates) | F | R | – | – |
| Claims Officer | R°(PII min) | R°(coverage @ svc date) | **–** | R°(code+fulfilment ref) | R°(auth scope) | R°(tariff/contract) | F(claims, batches, adj.) + A(line decide) | R°(claims KPIs) | – | – |
| Claims Reviewer (Senior) | R°(PII min) | R°(coverage @ svc date) | **–** | R°(code+fulfilment ref) | R°(auth scope) | R°(tariff/contract) | F + A/dual-control(override, high-value adj.) | R(claims KPIs) | – | – |
| Provider Admin | – | – | – | R°(own ops) | – | F(own) | R°(own) | R°(own) | W(own users) | – |
| Network Team | – | R°(contract) | – | – | – | F(metadata) | R°(rates) | R | – | – |
| Org Admin | R°(dir) | R°(config) | – | – | – | R° | – | R°(ops) | F(tenant) | R°(access rev.) |
| Super Admin | R°(BG) | R°(BG) | R°(BG) | R°(BG) | R°(BG) | R°(BG) | R°(BG) | R | F(global) | R°(access) |
| *Audit service* | – | – | – | – | – | – | – | – | – | append-only |

`BG` = break-glass only (time-boxed, dual-control, loudly audited). Note the **hard rules** visible in this table: Reception `emr = –`; Doctors `emr` is treating-scoped; Labs/Imaging `emr = R°(indication)` and no prescription/consume-rx; Pharmacies no lab/imaging results; Finance `emr = –` and no diagnosis; Approval `emr = R(clinical)`; **Claims Officer / Claims Reviewer `emr = –`** — no diagnosis, no notes, no result *values* (result existence + date + document reference only, as proof-of-service); **Branch/Clinic Manager `emr = –`**; **Call Center / Call Centre Supervisor `emr = –`** — and every non-`emr` cell in those two rows is additionally gated on a **passed caller verification** bound to the current call.

**Scope mode applies on top of every cell.** Each row is *additionally* narrowed by the role's scope mode (§2): a **BranchScoped** row (Reception, Appointment Coordinator, Nurses, Doctors' operational lists, Branch/Clinic Manager) returns only the **active branch**; a **MemberScoped** row spans all branches; a **ProviderScoped** row is unchanged. Scope mode never *widens* a cell — it can only narrow it. Independently, **any result whose `sensitivity_level` ≠ `Standard` collapses to existence-only for every role except the authoring/ordering doctor**, regardless of what this table grants ([37](37-branch-scoping-and-clinical-sensitivity.md)).

---

## 6. Data-minimization highlights (traceability to hard rules)

| Hard rule (from Foundations) | Roles affected | How this doc encodes it | Enforced where |
|---|---|---|---|
| Reception cannot view EMR | Reception | §3.2; `emr = –` in §5 | Gateway + service + RLS + field |
| Doctors view only patients they treat | Doctors, Nurses | §3.4/§3.5; `beneficiary:treating` scope | ABAC treating-relationship |
| Labs cannot view prescriptions | Labs (+ Imaging) | §3.6/§3.7 | Field-level deny in [Perm Matrix](11-permission-matrix.md) |
| Pharmacies cannot view investigation results | Pharmacies | §3.8 | Field/object deny; derived safety flags only |
| Finance cannot view diagnoses | Finance | §3.12; `emr = –` | Field-level mask on claim |
| Claims Officer cannot view diagnoses / EMR notes / result **values** | Claims Officer, Claims Reviewer | §3.17/§3.18; `emr = –` in §5 | Field-level deny on the claims projection ([Perm Matrix §4](11-permission-matrix.md)) |
| Result *existence* is proof-of-service, not clinical content | Claims Officer, Claims Reviewer | §3.17 | Projection exposes `exists`/`resulted_at`/`document_ref` only; `value` stripped |
| Medical-necessity questions leave the claims role entirely | Claims Officer → Medical Approval/Director | §3.19 | Route-to-clinical-review; opinion returns, visibility does not |
| Approval CAN view EMR/notes/reports | Medical Approval, Medical Director | §3.9/§3.10 | ABAC purpose = utilization-review |
| **Nothing is disclosed to a caller before identity is verified** | Call Center, Call Centre Supervisor | §3.3 verify-before-disclose hard rule; §3.21 | Server-side verification gate: ≥2 identifier types, bound to interaction + beneficiary; otherwise **403 + audited** |
| **Call Centre cannot view any clinical data** | Call Center, Call Centre Supervisor | §3.3/§3.21; `emr = –` in §5 | Field-level deny + server-side projection ([Perm Matrix §3.2/§4](11-permission-matrix.md)) |
| **Only identifier *types* are stored, never identifier values** | Call Center, Call Centre Supervisor | §3.3 | `caller_verification.verified_identifiers` holds types only; values stay in patient-service and are never echoed to the agent |
| **Branch-scoped roles reach only the active branch** | Reception, Appointment Coordinator, Nurses, Doctors (operational lists), Branch/Clinic Manager | §2 scope-mode table; per-role **Scope mode** | ABAC `BSC` + `RowScope.BranchIds` (+ optional RLS); cross-branch = **403**, not empty |
| **Sensitive results are existence-only to everyone but the author** | *All* roles except the authoring/ordering doctor — **including Medical Approval and Case Managers** | §3.9 sensitivity carve-out; §3.10 | Server-side projection: category+date+status+branch+`RESTRICTED` only; content requires an active grant |
| **Release of a sensitive report needs a justified, decided request** | Requester (any role) → authoring doctor **or** Medical Director | §3.4/§3.10 | Mandatory `purpose_code` + justification; time-boxed, single-result, non-transferable grant; every read-under-grant separately audited |

---

## 7. Role assignment & Segregation of Duties (SoD)

**Assignment model.** Roles are assigned as **group memberships in Keycloak** (see [Security Model §3](18-security-model.md)). Group membership → app role claim in the token → RBAC decision at the gateway/service. Provider-side users are assigned by their **Provider Admin** within a platform-sanctioned catalog; internal users by **Org Admin**; tenant/platform scope by **Super Admin**. Every assignment is audited (actor, subject, role, justification, time) per [Audit Strategy](19-audit-strategy.md).

**Assignment guardrails.**
- **Just-in-time elevation** (PIM-style) for T4/global capabilities and break-glass — no standing high privilege.
- **Access request + approval** workflow for any T3-reading role; approvals recorded.
- **Periodic access review** (quarterly for T3/T4) — reviewers confirm continued need-to-know; stale grants auto-expire.
- **Provider isolation** — a provider user can only be assigned provider-scoped roles for their own org.
- **Branch-assignment guardrails (Phase 14).** A user has **exactly one active `Home` branch** and zero or more `Additional` branches; the **permitted set = Home ∪ Additional**, filtered to `Active` and inside the validity window. **No user may create, extend or approve their own branch assignment** — not Reception, not a Branch/Clinic Manager, not a Doctor. `user_branch_assignment` is maintained by **Org Admin** (internal staff) and `practitioner_branch_assignment` by the **Network Team** (clinical rota/credentialing), each on a recorded request; both are **audited** (actor, subject, branch, type, validity, justification) and **revocation takes effect immediately** — the next request re-evaluates the permitted set. Granting a branch is an administrative act that confers **no data read** of its own.
- **Active branch is server-authoritative** — the `X-Active-Branch` header is a *hint*. Every request re-validates it against the permitted set; outside the set → `403` + audited `BranchScopeDenied`; absent → defaults to Home. Switching emits an audited `ActiveBranchSwitched` event.

**Segregation-of-Duties matrix (incompatible role pairs — must not be held by the same person).**

| Role A | Conflicts with | Why (risk) |
|---|---|---|
| Doctors (originates approval request) | Medical Approval / Medical Director (adjudicates that case) | Self-approval of own clinical request |
| Finance – Payment Initiate | Finance – Payment Release | Fraudulent payment through single actor |
| Beneficiary Mgmt – create/merge | Beneficiary Mgmt – merge approver | Fabricated/duplicated beneficiary |
| Provider Admin (grants roles) | Any clinical role they self-grant | Self-elevation to PHI access |
| Org Admin (grants roles) | Super Admin (approves elevation) | Unilateral privilege escalation |
| Network Team (sets rates) | Finance – Payment Release | Rate manipulation + self-pay |
| Claims – originator/submitter of a claim (provider user, or Mersal staff submitting on their behalf, or the reimbursement requester) | Claims Officer / Claims Reviewer **adjudicating that same claim** | Self-adjudication of a claim one raised |
| Any **provider-affiliated** role (Provider Admin, Labs, Imaging, Pharmacies, provider clinicians) | Claims Officer / Claims Reviewer **for claims of that provider** | A provider deciding its own money; Claims Officer is `tenant:own`, never `provider:own` |
| Claims – adjudication (decide lines / close batch) | Finance – Payment Release (settlement release) | Single actor could both approve and pay |
| Claims Officer who records an override / high-value adjustment | Claims Reviewer (Senior) approving that same override/adjustment | Dual control defeated; unchecked write-off, deduction or clawback |
| Claims – batch `Decided` / settlement advice issuer | Finance – Payment Initiate **and** Payment Release (the existing pair stays split) | Preserves the initiate ≠ release split end-to-end from adjudication to disbursement |
| Any user (incl. Branch/Clinic Manager) | **Granting/approving their own `user_branch_assignment` or `practitioner_branch_assignment`** | Self-widening of the branch scope; assignment is Org Admin (staff) / Network Team (practitioners) |
| Requester of a `report_access_request` | **Decider of that same request** (authoring doctor or Medical Director) | Self-release of a restricted sensitive report |
| Medical Director deciding a sensitive release | Being the **requester** or the grantee of that release | Same conflict; a Director decision is additionally flagged and extra-audited |

**SoD enforcement.** The policy engine evaluates SoD constraints at *assignment time* (prevent incompatible grant) and at *decision time* (deny an action if the subject is conflicted for the specific record — e.g., adjudicating a case they authored). Violations and overrides are audited as high-severity events.

---

## 8. RBAC + ABAC: how a role becomes a decision

A role by itself never authorizes access to a record. The pipeline is:

1. **Authenticate** (Keycloak, OIDC) → token with `role`, `tenant`, `provider`, `sub` claims + MFA/device state.
2. **RBAC gate** (Kong + service): does this role hold the permission for this resource+action at all? If no → deny.
3. **ABAC gate** (OPA/Cerbos): do the *attributes* satisfy the condition? e.g., `treating-relationship == true`, `provider-ownership match`, `tenant match`, **`branch-scope match` (resource `branch_id` ∈ permitted set and, for BranchScoped roles, == active branch)**, `order-status in {routed,accepted}`, `purpose == utilization-review`, `break-glass active`.
4. **Row-Level Security** (PostgreSQL): predicate filters the result set to scope (tenant, provider, **and — as defence in depth on branch-scoped tables — branch**).
5. **Field-Level filtering** (service/view): masks/removes fields the role must not see (e.g., diagnosis for Finance) — **including the sensitivity gate, which collapses a non-`Standard` result to existence-only unless the caller is the authoring doctor or holds an active grant**.
6. **Audit** (always): the decision + PHI-read is recorded.

The concrete conditions and example policy rules are in the [Permission Matrix §5–6](11-permission-matrix.md); the enforcement points are detailed in the [Security Model §4](18-security-model.md).

---

## 9. Cross-references
- Fine-grained, field-level, enforceable rules → **[11-permission-matrix.md](11-permission-matrix.md)**
- Enforcement mechanics, Zero-Trust, break-glass → **[18-security-model.md](18-security-model.md)**
- What/how every access is logged → **[19-audit-strategy.md](19-audit-strategy.md)**
- Regulatory mapping (HIPAA/GDPR/PDPL/UNHCR) → **[20-compliance-checklist.md](20-compliance-checklist.md)**
- Role↔service mapping and portals → **[0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)**
- Claims roles, hand-offs, batching, settlement → **[36-claims-management.md](36-claims-management.md)**
- Scope modes, branch model, practitioner specialty, sensitivity gating & release workflow → **[37-branch-scoping-and-clinical-sensitivity.md](37-branch-scoping-and-clinical-sensitivity.md)**

> **Change control:** any change to a role's scope, tier, or SoD conflicts must be reflected simultaneously here and in the Permission Matrix, and reviewed by the DPO + Security Architect before the policy bundle is deployed.
