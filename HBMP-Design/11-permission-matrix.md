# 11 — Permission Matrix (Fine-Grained, Enforceable)

[⬅ Back to Index](00-README-INDEX.md) · [Design Foundations](0A-DESIGN-FOUNDATIONS.md)

**Siblings:** [10-role-matrix.md](10-role-matrix.md) · [18-security-model.md](18-security-model.md) · [19-audit-strategy.md](19-audit-strategy.md) · [20-compliance-checklist.md](20-compliance-checklist.md) · [37-branch-scoping-and-clinical-sensitivity.md](37-branch-scoping-and-clinical-sensitivity.md)

> **Authority.** This document is the **source of truth** for authorization on HBMP. It defines, for every **role × resource × action**, whether access is permitted, and — for sensitive **fields** — whether the field is visible, masked, or denied. It encodes the ABAC conditions and provides example policy rules in pseudo-Rego/Cerbos that the policy engine (OPA/Cerbos) enforces. The narrative rationale is in the [Role Matrix](10-role-matrix.md); enforcement wiring is in the [Security Model](18-security-model.md).

---

## 1. Legend

**Actions**

| Token | Action | Meaning |
|---|---|---|
| **C** | Create | Create a new object |
| **R** | Read | Read an existing object (subject to field rules) |
| **U** | Update | Modify an existing object |
| **D** | Delete | Soft-delete/deactivate (hard delete is reserved + audited) |
| **A** | Approve | Adjudicate/authorize (approvals, payments, merges) |
| **X** | Consume | Record fulfillment/consumption against an order/benefit (dispense, collect, complete) |
| **E** | Export | Extract data out of the platform (download, report export, API bulk) |
| **DC** | Decide | Record a **claim-line** adjudication decision (approve / partially approve / deny / request-info / route-to-clinical-review) — distinct from `A` (clinical approval / payment release) |
| **AJ** | Adjust | Record an append-only claim adjustment (price/quantity correction, deduction, recovery, write-off, reallocation) |
| **B** | Batch | Create/manage a claim batch — add/remove claims, transition batch state, issue settlement advice |
| **V** | Void | Compensating reversal of a submitted claim/decision (never a delete; always paired with a reason + audit) |

**Decision values**

| Symbol | Meaning |
|---|---|
| ✅ | Allowed within role scope (no extra condition beyond scope + tenant) |
| 🟠 | Allowed **only if** ABAC condition holds (see condition code in cell or §5) |
| 🔒 | Allowed but **field-masked/minimized** (object visible, sensitive fields removed) |
| 🧨 | Allowed **only via break-glass** (time-boxed, dual-control, loud audit) |
| ❌ | Denied |
| — | Not applicable to this role/resource |

**ABAC condition codes** (defined fully in §5): `TR` treating-relationship · `PO` provider-ownership · `TEN` tenant-match · `ASG` assignment · `OST` order-status · `PUR` purpose-binding · `SOD` segregation-of-duties clear · `BG` break-glass active · `CNA` claims-originator-not-adjudicator · `NPA` not-provider-affiliated · `DCT` dual-control-above-threshold · `BOS` batch-open-single-membership · **`BSC` branch-scope** · **`SGA` sensitive-grant-active** · **`CVP` caller-verification-passed**.

**Sensitivity fields tracked at field level:** `diagnosis`, `emr_note`, `prescription`, `lab_result`, `imaging_result`, `financials` (amounts/claims), `pii` (identity/registration), `refugee_ref` (UNHCR/registration ID), **`sensitive_result`** (the content of any result whose `sensitivity_level` ≠ `Standard`).

**Result split (claims minimization):** every result-bearing field is projected as two independent things — `*_result.existence` (that a result exists, its `resulted_at` date and its `document_ref`) and `*_result.value` (the clinical content: values, units, flags, narrative, report body). A role may be allowed the **existence** and denied the **value**. This is what lets a Claims Officer verify *service was rendered* without ever reading a result.

**Sensitivity split (clinical minimization — Phase 14, [37](37-branch-scoping-and-clinical-sensitivity.md)):** the same two-part projection is applied a second time, on a *clinical* rather than a financial axis. Every order line and result carries a **denormalized `sensitivity_level`** ∈ {`Standard`, `Sensitive`, `HighlySensitive`} (pinned from the `examination_type` at order creation) plus a `sensitive_category` ∈ {`MentalHealth`, `HIV_STI`, `Genetic`, `SubstanceUse`, `ReproductiveHealth`, `GBV_Forensic`, `Other`}. Where the level is **not** `Standard`:
- **`*_result.existence+`** — category, `resulted_at`, order/result status, ordering **branch**, and a `RESTRICTED` marker — is the **only** projection for every principal except the authoring/ordering doctor;
- **`*_result.value`** and the **report `document`** are **default-deny**, released only under an active `report_access_grant` (`SGA`).

This split **overrides** every wider grant elsewhere in this document — including Medical Approval's `PUR` clinical read. A restricted result is never silently widened by a role, a purpose, or a case link.

**Branch dimension (Phase 14):** roles carry a **scope mode** ([10 §2](10-role-matrix.md)) — `BranchScoped`, `MemberScoped`, `ProviderScoped`. Every branch-scoped resource row carries `branch_id`; the `BSC` condition narrows reads to the caller's **active branch**. Branch scoping only ever **narrows**; it never substitutes for `TR`, `PO`, `ASG` or the field rules in §4.

---

## 2. Resource catalog

Resources map to microservices (see [0A](0A-DESIGN-FOUNDATIONS.md)). Object-level and field-level rules follow.

| Resource | Owning service | Key sensitive fields |
|---|---|---|
| `patient-profile` *(Phase 20)* | **profile** *(composes; owns nothing)* | every class below — see the §3b section matrix, which is the hard rule for this resource |
| `patient-photo` *(Phase 20)* | policy *(document link)* | `identity` — biometric-adjacent; a NARROWER allow-list than the profile itself |
| `beneficiary` | patient | `pii`, `refugee_ref` |
| `household` | patient | `pii` |
| `policy` / `benefit_plan` | policy | `financials` (limits) |
| `eligibility_check` | eligibility | verdict only |
| `emr_record` | emr | `emr_note`, `diagnosis` |
| `clinical_note` | emr | `emr_note`, `diagnosis` |
| `diagnosis` | emr | `diagnosis` |
| `order` (lab/imaging/procedure) | orders | indication, `lab_result`/`imaging_result` |
| `prescription` | orders | `prescription` |
| `prescription_validation` *(26.4)* | pharmacy | `diagnosis`, `prescription` — the findings name the drugs AND the diagnoses they were checked against |
| `prescription_line_override` *(26.4)* | pharmacy | `prescription` — the prescriber's free-text clinical reason for proceeding past a warning |
| `masterdata_catalogue` *(26.1)* | masterdata | none — public medical reference data (ICD/CPT/LOINC/ATC/drugs/indications/interactions/allergens), tenant-free and carrying no PHI |
| `lab_result` | orders | `lab_result` |
| `imaging_result` | orders | `imaging_result` |
| `approval_case` | approvals | attached clinical evidence |
| `provider` / `contract` / `catalog` | provider | `financials` (rates) |
| `claim` / `invoice` / `payment` | finance (reporting) | `financials`, service codes |
| `claim` | claims | `financials`, service codes, provider, member ref |
| `claim_line` | claims | `financials`, service code, quantity, fulfillment ref, `auth` ref |
| `claim_decision` | claims | decision, allowed amount, reason codes, rationale, decider (append-only) |
| `claim_adjustment` | claims | signed amount delta, adjustment type, reason code, rationale (append-only) |
| `claim_batch` | claims | rollup `financials`, payee provider/branch, period |
| `reimbursement_request` | claims | `pii` (member), receipt `financials`, OCR candidates + confidence |
| `claim_document` | claims (document) | invoice/receipt/proof-of-service scans — **clinical attachments are reference-only for claims roles** |
| `settlement_advice` | claims | net payable, per-line detail, payee — immutable, WORM-stored |
| `user` / `role_binding` | identity | admin metadata |
| `audit_event` | audit | append-only |
| `document` (reports, DICOM ref) | document | clinical attachments |
| `branch` *(Phase 14)* | provider *("network & facilities")* | operational reference data — **no PHI**; `branch_code` is a stable reporting key |
| `user_branch_assignment` *(Phase 14)* | identity | admin metadata — `assignment_type` ∈ {`Home`,`Additional`}, validity window; **self-grant is forbidden** |
| `practitioner` *(Phase 14)* | provider | `pii` (name, `license_no`, `license_expiry`) — clinical profile behind a user |
| `specialty` / `practitioner_specialty` *(Phase 14)* | provider (reference) | none — reference data; one specialty flagged `is_primary` |
| `examination_type` *(Phase 14)* | masterdata (reference) | none directly, but **carries `sensitivity_level` + `sensitive_category`**, which govern §3.5/§4 |
| `report_access_request` *(Phase 14)* | orders | `purpose_code`, **`justification` (free text — may itself hint at clinical context)**, requester role, decision reason |
| `report_access_grant` *(Phase 14)* | orders | `grantee_user_id`, `result_ref`, `expires_at`, `purpose_code` — **single-result, non-transferable** |
| `call_interaction` *(Phase 15)* | callcentre | `call_ref`, `beneficiary_id` (`pii` link), agent, reason code, outcome, free-text `notes` — **contact-centre operational data, no clinical content** |
| `caller_verification` *(Phase 15)* | callcentre | `verified_identifiers` — **which identifier *types* were confirmed, never the values** — plus result, `failure_reason`, verifier, timestamp |

---

### 2b. `masterdata:read` — the reference catalogue (26.1)

Added in phase 26.1, and it **reverses a position recorded in code**: masterdata-service served its whole
catalogue behind a bare authenticated check, and its own authorization suite argued that this was correct.

The grant is deliberately **broad — every role that holds any scope**. That is not an oversight and it does
not weaken anything: a diagnosis code means the same thing to a doctor, a pharmacist and a claims officer,
the catalogue is tenant-free, and withholding it would break their screens while protecting nothing. Roles
holding `profile:read` need it too — the patient profile resolves ICD codes to titles through masterdata,
and without the scope every profile silently degrades to raw codes.

What the scope buys is not restriction:

- reference-data reach becomes a **stated, reviewable, revocable line in this matrix** rather than an
  unstated consequence of holding any token at all;
- a **service, integration or partner token must ask** for the catalogue instead of receiving it by default,
  and the set of codes a platform carries is a fingerprint of what it treats;
- phase 27's `approval_supervisor` has something real to be granted.

There is deliberately **no `masterdata:write`**. Master data changes through admin-service's governed,
effective-dated, audited path (8b.2), never through this service.

> **Consequence worth knowing:** every service sets `Auth:ProtectedScopeRequiresMfa=true`, so scope-gating
> the catalogue also imposes MFA on it. This is consistent rather than a regression — any session that can
> reach `emr:read` is already MFA-backed.

### 2c. `auth:request-substitution` and the authorization register (ADR-0034)

Two grants land together, and the second is the one that widens a disclosure surface.

**`auth:request-substitution`** — held by `lab_tech` and `imaging_tech`. It authorizes exactly one endpoint,
`POST /authorizations/substitution-requests`, whose body names an order line, a reason, and optionally a
proposed code. It carries **no decision authority**: the request lands `Submitted` in the approval team's
normal queue with the normal SLA clock, and `auth:decide` is not granted here.

Pharmacists are deliberately **not** granted it. They already resolve the same question at the counter
against a real formulary — the drug's ATC-5 class — and pharmacy-service already routes an off-formulary
request to approvals on its own. A second way to ask would be a second answer to keep in step with the first.

The scope exists at all because **examinations have no equivalence set anywhere in master data**:
`examination_type` records a category and a sensitivity, and neither says that one test may stand in for
another. Offering a technician a list derived from the category would put "any radiology procedure" behind a
button, which is a technician prescribing.

**The authorization register** — `GET /authorizations?kind=Fulfilment` and `GET /authorizations/{id}/items`,
both under the existing `auth:read`. This is a **widening**: the approval team could previously see only the
requests they were asked to decide, and everything the platform authorized by rule rather than by review —
which is almost everything — was invisible to a team accountable for what the payer pays.

It is bounded three ways. The item projection carries **codes, labels, quantities and, only where the
delivered code differs from the written one, the substituting pharmacist's reason** — no diagnosis, no note,
no indication; the schema has no field that could carry one. The reason is the same bounded exception §3.2
already makes for a validity-extension request: it is logistics written by a pharmacist and is the entire
substance of what a reviewer is looking at, and routing them through the PHI-audited clinical review view to
read one sentence would add an audited access to a patient's record for a question that is not about the
patient. And the **default is unchanged** — `kind` defaults to `Review`, so the reviewer inbox does not fill
with dispenses; the register is a deliberate ask on its own screen.

### 2d. `policy:price-lookup` — the pricing slice, not the plan book

An ACTION, not a scope: satisfied by `policy:read` **or** `eligibility:check`, and it authorizes exactly two
routes — `GET /plans/{id}/version-at` and `GET /plan-versions/{id}/cost-share`.

Both sat behind `policy:read`, which is the entire benefit product: every payer, every plan, every version
and every rule on the platform. A pharmacist quoting at a counter does not hold it and should not — the same
over-grant `practitioner:read` was split out of `provider:read` to avoid.

**What that cost, until it was found.** The shared pricing path forwards the fulfiller's own token, so every
quote made at a counter took a 403 — and the client could not tell that refusal apart from "this plan does
not price pharmacy at the resolved tier", so a permission error was reported to a patient as a fact about
their benefit. It stayed invisible because the sentence was ALSO true: no plan version had a pharmacy rule,
so a broken route and a correct answer looked identical from outside.

No new scope was minted. `eligibility:check` is already the scope for "what does this member pay for this
category at this provider", which is precisely the question these two routes serve; a third grant naming the
same question would be one more thing to reason about and one more place to revoke (identity 0025 made this
argument for the pharmacist, and 0026 for the bench).

## 3. Object-level permission matrix

Cells show allowed actions with their decision symbol. Absent actions are denied. **All ✅/🟠 reads are still subject to field-level rules in §4.**

### 3.1 Beneficiary & identity data

| Role | beneficiary | household | policy/plan | eligibility_check |
|---|---|---|---|---|
| Beneficiary Mgmt | C✅ R✅ U✅ D🟠(SOD) | C✅ R✅ U✅ | R✅ U🟠(assign) | R✅ |
| Reception | R🔒(pii min) 🟠TR/ASG | — | R🔒(verdict) | C✅ R🔒 |
| Call Center | R🔒🟠CVP U🟠(contact, CVP) | R🔒🟠CVP | R🔒(coverage + remaining limits)🟠CVP | C✅ R🔒🟠CVP |
| **Call Centre Supervisor** | R🔒🟠CVP U🟠(contact, CVP) | R🔒🟠CVP | R🔒(coverage + remaining limits)🟠CVP | C✅ R🔒🟠CVP |
| Doctors | R🟠TR | R🟠TR | R🔒🟠TR | R🔒 |
| Nurses | R🟠(TR/ASG) | — | R🔒 | R🔒 |
| Labs | R🔒🟠(PO+OST) | — | — | — |
| Imaging | R🔒🟠(PO+OST) | — | — | — |
| Pharmacies | R🔒🟠(PO+OST) | — | R🔒(drug cov)🟠 | R🔒🟠 |
| Medical Approval | R✅ | R✅ | R✅ | R✅ |
| Medical Director | R✅ | R✅ | R✅ | R✅ |
| Case Managers | R🟠ASG | R🟠ASG | R🟠ASG | R🟠ASG |
| Finance | R🔒(pii min) | — | R✅(financial) | R🔒 |
| **Claims Officer** | R🔒(pii min: member no., name, DOB) | — | R🔒(coverage @ service date) | R🔒(verdict @ service date) |
| **Claims Reviewer** | R🔒(pii min) | — | R🔒(coverage @ service date) | R🔒(verdict @ service date) |
| Provider Admin | ❌ | ❌ | ❌ | ❌ |
| Network Team | ❌ | ❌ | R🔒(contract) | ❌ |
| Org Admin | R🔒(dir)🟠 | ❌ | R🔒(config) | ❌ |
| Super Admin | R🧨 | R🧨 | R🧨 | R🧨 |

### 3.2 Clinical / EMR data — the core minimization zone

| Role | emr_record | clinical_note | diagnosis | prescription | lab_result | imaging_result |
|---|---|---|---|---|---|---|
| Beneficiary Mgmt | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Reception** | **❌** | **❌** | **❌** | **❌** | **❌** | **❌** |
| **Call Center** | **❌** | **❌** | **❌** | **❌** | **❌** | **❌** |
| **Call Centre Supervisor** | **❌** | **❌** | **❌** | **❌** | **❌** | **❌** |
| **Doctors** | C🟠TR R🟠TR U🟠TR | C🟠TR R🟠TR U🟠TR | C🟠TR R🟠TR | C🟠TR R🟠TR | R🟠TR | R🟠TR |
| Nurses | R🟠(TR/ASG) U🟠(nursing) | C🟠(nursing) R🟠 | R🟠(problem list) | R🔒(admin only) | R🟠(TR) | R🟠(TR) |
| **Labs** | R🔒(indication)🟠(PO+OST) | ❌ | R🔒(indication only)🟠 | **❌** | C🟠(PO+OST) R🟠 U🟠 | ❌ |
| **Imaging** | R🔒(indication)🟠(PO+OST) | ❌ | R🔒(indication only)🟠 | **❌** | ❌ | C🟠(PO+OST) R🟠 U🟠 |
| **Pharmacies** | R🔒(rx context)🟠 | ❌ | ❌ | R🟠(PO+OST) X🟠 | **❌** | **❌** |
| **Medical Approval** | R✅🟠PUR | R✅🟠PUR | R✅🟠PUR | R✅🟠PUR | R✅🟠PUR | R✅🟠PUR |
| Medical Director | R✅🟠PUR | R✅🟠PUR | R✅🟠PUR | R✅🟠PUR | R✅🟠PUR | R✅🟠PUR |
| Case Managers | R🔒(summary)🟠ASG | R🔒(summary)🟠ASG | R🔒(coord)🟠ASG | R🔒🟠ASG | R🔒🟠ASG | R🔒🟠ASG |
| **Finance** | ❌ | ❌ | **❌** | ❌ | ❌ | ❌ |
| **Claims Officer** | **❌** | **❌** | **❌** | ❌ | **❌** value → R🔒 existence only | **❌** value → R🔒 existence only |
| **Claims Reviewer** | **❌** | **❌** | **❌** | ❌ | **❌** value → R🔒 existence only | **❌** value → R🔒 existence only |
| Provider Admin | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Network Team | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Org Admin | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Super Admin | R🧨 | R🧨 | R🧨 | R🧨 | R🧨 | R🧨 |

> **Hard-rule check (must always hold):** Reception row = all ❌. **Call Center / Call Centre Supervisor rows = all ❌** (no clinical data whatsoever — see §3.6). Doctors clinical = all 🟠TR. Labs `prescription` = ❌. Imaging `prescription` = ❌. Pharmacies `lab_result` = ❌ and `imaging_result` = ❌. Finance `diagnosis` = ❌ (and whole clinical row ❌). **Claims Officer / Claims Reviewer `diagnosis` = ❌, `emr_note` = ❌, `lab_result.value` = ❌, `imaging_result.value` = ❌** — only `*_result.existence` (exists + `resulted_at` + `document_ref`) is readable, as proof-of-service. Medical Approval clinical = R✅ under `PUR`. Any change breaking these must be rejected at review.
>
> **Clinical-review hand-off:** a claim line needing medical-necessity judgement is routed to `ClinicalReview`, where **Medical Approval / Medical Director** read the clinical context under `PUR` and record an **opinion**. Routing never widens the Claims Officer's projection, and the clinical reviewer gains **no** `DC`/`AJ`/`B` rights on the claim ([10 §3.19](10-role-matrix.md)).

### 3.3 Orders, approvals, provider, finance, admin, audit

| Role | order | approval_case | provider/contract/catalog | claim/invoice/payment | user/role_binding | audit_event |
|---|---|---|---|---|---|---|
| Beneficiary Mgmt | ❌ | ❌ | R🔒 | ❌ | ❌ | ❌ |
| Reception | R🔒(appt)🟠PO | ❌ | R🔒(own site)🟠PO | ❌ | ❌ | ❌ |
| Call Center | R🔒(status)🟠CVP C🟠(appt, CVP) U🟠(appt, CVP — cancel requires `reason_code`) | R🔒(status)🟠CVP | R🔒 | R🔒(balance)🟠CVP | ❌ | ❌ |
| **Call Centre Supervisor** | R🔒(status)🟠CVP C🟠(appt, CVP) U🟠(appt, CVP) | R🔒(status)🟠CVP | R🔒 | R🔒(balance)🟠CVP | ❌ | ❌ |
| Doctors | C🟠TR R🟠TR U🟠TR | C🟠TR R🟠TR | R🔒 | ❌ | ❌ | ❌ |
| Nurses | R🟠(care) | ❌ | R🔒 | ❌ | ❌ | ❌ |
| Labs | R🟠(PO+OST) X🟠 U🟠 | ❌ | R🔒(own)🟠PO | ❌ | ❌ | ❌ |
| Imaging | R🟠(PO+OST) X🟠 U🟠 | ❌ | R🔒(own)🟠PO | ❌ | ❌ | ❌ |
| Pharmacies | R🟠(PO+OST) X🟠 | ❌ | R🔒(own)🟠PO | C🟠(dispense claim) R🔒 | ❌ | ❌ |
| Medical Approval | R✅ | C✅ R✅ U✅ A✅🟠SOD | R🔒 | R🔒(status) | ❌ | ❌ |
| Medical Director | R✅ | R✅ U✅ A✅🟠SOD (override) | R🔒 | R🔒(cost) | ❌ | ❌ |
| Case Managers | R🟠ASG | C🟠ASG R🟠ASG U🟠ASG | R🔒 | R🔒🟠ASG | ❌ | ❌ |
| Finance | R🔒(billing code) | R🔒(status) | R🔒(rates) | C✅ R✅ U✅ A🟠SOD(release) E🔒 | ❌ | ❌ |
| **Claims Officer** | R🔒(code + fulfillment ref) | R🔒(auth status/scope) | R🔒(tariff/contract) | see §3.4 | ❌ | ❌ |
| **Claims Reviewer** | R🔒(code + fulfillment ref) | R🔒(auth status/scope) | R🔒(tariff/contract) | see §3.4 | ❌ | ❌ |
| Provider Admin | R🔒(own ops)🟠PO | ❌ | C🟠PO R🟠PO U🟠PO | R🔒(own)🟠PO | C🟠PO R🟠PO U🟠PO D🟠PO (own users) | ❌ |
| Network Team | ❌ | ❌ | C✅ R✅ U✅ A🟠SOD | R🔒(rates) | ❌ | ❌ |
| Org Admin | ❌ | ❌ | R🔒 | ❌ | C✅ R✅ U✅ D🟠SOD (tenant) | R🔒(access-review view) |
| Super Admin | R🧨 | R🧨 | R✅ | R🧨 | C✅ R✅ U✅ D✅ (global)🟠SOD | R🔒(read, cannot alter) |
| *audit service* | — | — | — | — | — | append-only (C only) |

**Export (E) note:** Export is a *distinct, elevated* action. Only Finance (financial reports, masked PII), Medical Director/Approval (case packets under `PUR`), Network Team (network reports), Claims Officer/Reviewer (settlement advice + claim registers, **no clinical fields**), Org/Super Admin (operational, no PHI content) and reporting-designated users may Export, always 🔒-masked and always audited as a high-severity `data.export` event. **No provider-side role may bulk-export beneficiary data.**

### 3.4 Claims & settlement (Phase 10b — see [36-claims-management.md](36-claims-management.md))

Actions here use the extended tokens from §1: **DC** Decide · **AJ** Adjust · **B** Batch · **V** Void (plus C/R/U/E).

| Role | claim | claim_line | claim_decision | claim_adjustment | claim_batch | reimbursement_request | claim_document | settlement_advice |
|---|---|---|---|---|---|---|---|---|
| **Claims Officer** | C✅ R✅ U🟠(pre-submit only) V🟠(CNA+NPA) | R✅ U🟠(manual pricing on `NO_TARIFF`) | C(DC)🟠(CNA+NPA) R✅ | C(AJ)🟠(CNA+NPA, ≤threshold) R✅ | C(B)✅ R✅ U(B)🟠BOS | R✅ U🟠(match/confirm OCR) DC🟠(CNA+NPA) | C✅ R🔒(non-clinical projection) | C✅ R✅ E🔒✅(audited, no clinical fields) |
| **Claims Reviewer (Senior)** | R✅ V🟠(CNA+NPA) | R✅ | R✅ **A🟠DCT** (approve override above threshold) | C(AJ)🟠(CNA+NPA) R✅ **A🟠DCT** (high-value) | R✅ U(B)🟠(remove from `UnderReview`, reason required) | R✅ DC🟠(CNA+NPA) | R🔒 | C✅ R✅ E🔒✅ |
| Medical Approval / Medical Director | R🔒(routed line only, non-financial context) | R🔒(routed line only) | R🔒 + **C(opinion)**🟠PUR — **DC ❌** | ❌ | ❌ | R🔒(routed line only) | R🟠PUR (clinical attachment for the routed line) | ❌ |
| Finance | R🔒(rollups) | R🔒 | R🔒 | R🔒 | R✅(rollups) | R🔒 | ❌ | R✅ E🔒 **A🟠SOD (payment release, external execution)** |
| Provider Admin / provider-side roles | C🟠PO R🟠PO U🟠PO(pre-submit) | R🟠PO | R🔒🟠PO (own outcome + reason codes) | R🔒🟠PO | R🔒🟠PO (own batches only) | ❌ | C🟠PO R🟠PO (own submissions) | R🔒🟠PO (own advice) E🔒🟠PO |
| Network Team | R🔒(aggregate variance) | ❌ | ❌ | R🔒(deduction terms) | R🔒(aggregate) | ❌ | ❌ | R🔒(aggregate) |
| Case Managers | R🔒🟠ASG (own case load's reimbursements) | ❌ | R🔒🟠ASG (status) | ❌ | ❌ | C🟠ASG R🟠ASG U🟠ASG (submit on behalf) | C🟠ASG R🟠ASG | ❌ |
| Beneficiary (self-service) | R🔒(self, own reimbursement only) | ❌ | R🔒(self: outcome + reason) | ❌ | ❌ | C🟠(self) R🟠(self) U🟠(self, pre-submit) | C🟠(self) R🟠(self) | ❌ |
| Reception | ❌ | ❌ | ❌ | ❌ | ❌ | C🟠(on behalf, at own site) R🔒🟠PO | C🟠 R🔒🟠PO | ❌ |
| Doctors / Nurses / Labs / Imaging / Pharmacies | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Org Admin | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Super Admin | R🧨 | R🧨 | R🧨 | R🧨 | R🧨 | R🧨 | R🧨 | R🧨 |
| *audit service* | — | — | — | — | — | — | — | — |

> **Claims hard-rule check (must always hold):**
> 1. Claims Officer / Claims Reviewer: `diagnosis` = ❌, `emr_note` = ❌, `lab_result.value` = ❌, `imaging_result.value` = ❌; `*_result.existence` (+ `resulted_at`, `document_ref`) = ✅ **as proof-of-service only**.
> 2. Every provider-side claims cell carries `PO` — a provider sees **only its own** claims, lines, batches, documents and settlement advice, never another provider's.
> 3. Beneficiary / Case Manager claims access is limited to **their own / their assigned case load's reimbursement** — never another member's claim, never a provider batch.
> 4. `DC` (decide) is **never** held by the claim's originator/submitter (`CNA`) nor by anyone provider-affiliated with the claiming provider (`NPA`).
> 5. `A` on `claim_decision`/`claim_adjustment` (dual control, `DCT`) is **never** exercised by the same principal who recorded the override/adjustment.
> 6. No role anywhere in this matrix holds an "execute payment" action — the platform issues settlement advice only.

### 3.5 Branch, practitioner & sensitivity resources (Phase 14 — see [37-branch-scoping-and-clinical-sensitivity.md](37-branch-scoping-and-clinical-sensitivity.md))

**3.5.1 Object-level.** `SW` (switch) is the active-branch switch action on `user_branch_assignment`; it is a **read of one's own permitted set + a context change**, never a grant.

| Role | branch | user_branch_assignment | practitioner | specialty | examination_type | report_access_request | report_access_grant |
|---|---|---|---|---|---|---|---|
| Reception / Appointment Coordinator | R✅ (permitted set) | R🔒(self) SW🟠(own permitted set) | R🔒(name, specialty, branch) | R✅ | R🔒(orderable name only) | ❌ | ❌ |
| Nurses | R✅ (permitted set) | R🔒(self) SW🟠 | R🔒 | R✅ | R🔒 | C🟠(TR, purpose+justification) R🟠(own requests) | R🔒🟠(own, active grants) |
| **Doctors** | R✅ (permitted set) | R🔒(self) SW🟠 | R🔒 U🟠(self profile) | R✅ | R✅ | C🟠(TR) R🟠(own + **those routed to them as author**) **A🟠(SOD: author, not requester)** | R🔒🟠(own) **D🟠(revoke: author)** |
| **Branch/Clinic Manager** | R✅ (assigned branches) U🟠(BSC: opening hours/availability of the **active** branch) | R🔒(staff of active branch — **C/U ❌**) SW🟠 | R🔒(coverage view: name, specialty, sessions) | R✅ | R🔒 | ❌ | ❌ |
| Medical Approval | R✅ (all) | ❌ | R🔒 | R✅ | R✅ | C🟠(PUR, purpose+justification) R🟠(own requests) | R🔒🟠(own, active grants) |
| **Medical Director** | R✅ (all) | R🔒(oversight) | R✅ | R✅ | R✅ U🟠(classification, with clinical governance) | R✅ **A🟠(SOD: alternate decider — flagged + extra-audited)** | R✅ **D🟠(revoke)** |
| Case Managers | R✅ (all) | ❌ | R🔒 | R✅ | R🔒 | C🟠(ASG, purpose+justification) R🟠(own requests) | R🔒🟠(own, active grants) |
| Finance / Claims Officer / Claims Reviewer | R🔒(code + name, as a reporting/batching dimension) | ❌ | R🔒(performing clinician ref only) | R🔒 | R🔒(code + name) | ❌ | ❌ |
| **Network Team** | C✅ R✅ U✅ D🟠(SOD) | ❌ | C✅ R✅ U✅ D🟠(SOD) | C✅ R✅ U✅ | R✅ | ❌ | ❌ |
| **Org Admin** | R✅ U🟠(SOD, org config) | **C✅ R✅ U✅ D🟠(SOD — never for self)** | R🔒 | R✅ | R🔒 | ❌ | R🔒(register view: who holds what, no content) |
| Super Admin | R✅ | R🧨 | R🧨 | R✅ | R✅ | R🧨 | R🧨 |
| DPO / compliance reviewer | R✅ | R🔒 | R🔒 | R✅ | R✅ | R🔒(register: purpose + decision, **not** the result) | R✅ **D🟠(revoke)** |
| Labs / Imaging / Pharmacies / Provider Admin | ❌ *(branch is internal to Mersal)* | ❌ | ❌ | R🔒(referral routing only) | R🔒(their own orderables) | ❌ | ❌ |
| Beneficiary (self-service) | ❌ | ❌ | R🔒(treating clinician name + specialty) | — | — | — *(the subject's own access is a data-subject right, not a grant)* | — |
| *audit service* | — | — | — | — | — | — | append-only (C only) |

**3.5.2 Sensitive results — the two-column projection.** For a result whose `sensitivity_level` ≠ `Standard` (`existence+` = category, `resulted_at`, status, ordering branch, `RESTRICTED` marker):

| Role | `sensitive_result.existence+` | `sensitive_result.value` / report `document` |
|---|---|---|
| **Authoring / ordering doctor** (with `TR`) | ✅ | **✅** — the only routine reader |
| Beneficiary (data subject, future portal) | ✅ | ✅ (own data) |
| Other treating clinicians (Doctors, Nurses) | ✅ | **❌** → 🟠`SGA` |
| **Medical Approval team** | ✅ | **❌** → 🟠`SGA` — *this deliberately overrides `PUR`* |
| **Medical Director** | ✅ | **❌** → 🟠`SGA` (may *decide* release without reading) |
| **Case Managers** | ✅ | **❌** → 🟠`SGA` |
| Reception / Appointment Coordinator / Branch Manager | ✅ (status only, no category where the category alone is identifying) | **❌** |
| Finance / Claims Officer / Claims Reviewer | ✅ (proof-of-service only: exists + date + `document_ref`) | **❌** — never, not even under a grant |
| Labs / Imaging / Pharmacies (performing provider) | ✅ | **✅ only for the result they authored**, `PO`+`OST`; ❌ for any other |
| Reporting / analytics | ✅ **aggregated & de-identified only** | **❌** |
| Super Admin / emergency clinician | ✅ | 🧨 **loud break-glass** — notifies author + Medical Director + DPO, mandatory retrospective review |

> **Hard-rule check (must always hold) — branch & sensitivity:**
> 1. **(a) Branch scope is a denial, not a filter.** A `BranchScoped` role (Reception, Appointment Coordinator, Nurses, Doctors' operational lists, Branch/Clinic Manager) may read a branch-scoped resource **only where `resource.branch_id == subject.active_branch`** *and* the active branch ∈ the subject's permitted set (Home ∪ Additional, `Active`, in-window). A cross-branch request returns **`403` + audited `BranchScopeDenied`** — **never** an empty `200`. `MemberScoped` roles are unrestricted by branch; `ProviderScoped` roles are unaffected.
> 2. **(b) Sensitive results are default-deny for everyone but the author.** Where `sensitivity_level != 'Standard'`, `result.value` and the **report document** = **❌ for every role — including the medical approval team and the Medical Director** — unless an **active, unexpired, unrevoked `report_access_grant`** exists for `(subject, result)` (`SGA`). `existence+` (category, date, status, branch, `RESTRICTED` marker) = **✅**. Claims/Finance never receive the value, grant or no grant.
> 3. **(c) A grant is single-result and non-transferable.** One `report_access_grant` covers **exactly one `result_ref` for exactly one `grantee_user_id`**; it cannot be re-scoped, widened, shared, delegated, or inherited by a role or a team queue. It is **time-boxed** (default 72 h `Sensitive` / 24 h `HighlySensitive`, configurable), auto-expires, is revocable by the author / Medical Director / DPO, and **every read under it is audited separately** with `grant_id`, `purpose_code` and actor.
> 4. **Release requires a decided request.** A `report_access_grant` may exist **only** as the product of an `Approved` `report_access_request` carrying a **mandatory `purpose_code` + free-text `justification`**, decided by the **authoring/ordering doctor or a Medical Director** (`SOD`: requester ≠ decider). A Director decision is flagged `decided_by_role=MedicalDirector` and **extra-audited**.
> 5. **Branch scoping never replaces an existing control.** `BSC` composes with `TR`/`PO`/`ASG`/`TEN` and with the §4 field rules — it narrows, it never grants.

### 3.5b Branch-management resources (Phase 25 — see [42-branch-management.md](42-branch-management.md))

**THE RULE THAT GOVERNS THIS WHOLE SECTION:** `branch_coordinator` and `clinics_manager` hold an **identical**
scope set. Every cell below applies to both. They differ only in **reach** — one active branch versus the
whole permitted set — and reach is grant-derived, never role-derived.

| Resource | Branch Coordinator | Clinics Manager | Everyone else |
|---|---|---|---|
| `practitioner` (assign/revoke, specialty, licence) | **W** 🔒 branch-reach | **W** 🔒 branch-reach | `provider:write` (network team) only |
| `practitioner.license_no` | **R** (field-masked to the maintaining scopes) | **R** | absent from the payload |
| `practitioner.license_expiry` | **R/W** | **R/W** | **R** — the DATE is not the NUMBER; it is what a status chip renders |
| `specialty` catalogue | **R** (assign from the seeded 26) | **R** | create/rename stays `provider:write` |
| `roster_exception` | **W** 🔒 branch-reach | **W** 🔒 branch-reach | — |
| `inventory.item` | **R/W** | **R/W** | — |
| `inventory.stock_movement` | **W** (append-only) 🔒 branch-reach | **W** 🔒 branch-reach | — |
| `branch` (create/retire) | **✗** | **✗** | `provider:write` only |
| external provider / contract / tariff | **✗** | **✗** | `provider:write` / `provider:admin` |
| `emr_note`, `diagnosis`, result values | **✗** | **✗** | unchanged |

**New scopes:** `branch:practitioner:write`, `branch:roster:write`, `branch:inventory:read`,
`branch:inventory:write`.

**HARD RULES**

1. **No `provider:write` for a branch role, ever.** It is network-wide — it creates branches, edits external
   labs, pharmacies and tariffs, and unmasks `license_no`. A coordinator maintaining a doctor's licence must
   never acquire the authority to re-price the network to do it.
2. **The branch-reach check is not the scope check.** Holding `branch:*:write` says *what* you may do; reach
   says *where*. A caller holding only a branch scope may act **only on branches in reach** — a coordinator at
   Maadi assigning a practitioner to Dokki is **403 + audited at High**, never a silent success. Widening a
   scope group without this check is strictly worse than not widening it: it hands every coordinator the whole
   network while looking, in the route table, like a carefully sized permission.
3. **Inventory carries no beneficiary identifier** — not in a route, a request body, an entity or a column.
   Clinic inventory is not a second dispensing path; prescribed items go through `pharmacy-service`.
4. **Licence numbers stay field-masked.** Widened to the branch-maintaining scope in Phase 25, and
   deliberately NOT to `practitioner:read` — reception holds that for the booking pickers, and a licence
   number is not something the front desk needs to book an appointment.

### 3.6 Call-centre resources (Phase 15 — see [10 §3.3/§3.21](10-role-matrix.md))

The contact centre owns two resources in the `callcentre` schema: `call_interaction` (one row per call, keyed by `call_ref`) and `caller_verification` (one row per verification attempt — **pass *and* fail**). Everything an agent does to a member is gated on `CVP` (§5) and correlated to the interaction.

| Role | call_interaction | caller_verification | member 360 (composed read) | appointment C/U **from a call** | contact U **from a call** |
|---|---|---|---|---|---|
| **Call Center** | C✅ R🟠(own calls) U🟠(own, while `Open`) | C✅ R🟠(own interaction) | R🔒🟠**CVP** *(pre-verification: match/no-match + name + challengeable identifier **types** only)* | C🟠**CVP** U🟠**CVP** *(cancel requires a `reason_code`)* | U🟠**CVP** |
| **Call Centre Supervisor** | C✅ R✅(**team**) U🟠(own, while `Open`) | C✅ R✅(**team**) | R🔒🟠**CVP** | C🟠**CVP** U🟠**CVP** | U🟠**CVP** |
| Case Managers | R🔒🟠ASG (calls of their own case load: reason, outcome, timestamps) | ❌ | — | — | — |
| Reception / Appointment Coordinator | ❌ | ❌ | ❌ | — *(books at its own branch under `BSC`, not from a call)* | — |
| Medical Approval / Medical Director | ❌ | ❌ | ❌ | ❌ | ❌ |
| Finance / Claims Officer / Claims Reviewer | ❌ | ❌ | ❌ | ❌ | ❌ |
| Doctors / Nurses / Labs / Imaging / Pharmacies / Provider Admin | ❌ | ❌ | ❌ | ❌ | ❌ |
| Network Team | ❌ | ❌ | ❌ | ❌ | ❌ |
| Org Admin | ❌ | ❌ | ❌ | ❌ | ❌ |
| Reporting / analytics | R🔒 **aggregate & de-identified only** (KPI read model — no `notes`, no `pii`) | R🔒 **aggregate only** (failure *rate*) | ❌ | ❌ | ❌ |
| DPO / compliance reviewer | R🔒(register: `call_ref`, reason, outcome, actor — oversight of disclosure, not the conversation) | R🔒(register: result + which identifier **types**) | ❌ | ❌ | ❌ |
| Super Admin | R🧨 | R🧨 | R🧨 | ❌ | ❌ |
| *audit service* | — | — | — | — | — |

> **Hard-rule check (must always hold) — call centre:**
> 1. **(a) Verify before you disclose.** The member 360 and **every** appointment (book/reschedule/cancel) and contact action from a call require an **active `caller_verification` with `result='Passed'` and ≥2 confirmed identifier types, bound to *this* `interaction_id` **and** *this* `beneficiary_id`** (`CVP`). Absent or failed ⇒ **`403` + audited `CallerVerificationRequired`** — **never** a partially-populated `200`. A verification **expires when the interaction closes** and is never inherited by a later call. Failed attempts are **persisted and audited**, never silently discarded.
> 2. **(b) The Call Centre sees no clinical data — absolutely.** `diagnosis`, `emr_note`, `lab_result`, `imaging_result`, `prescription` and **examination detail** = **❌** for Call Center and Call Centre Supervisor, with **no condition, purpose, grant or break-glass path** that lifts it. The only clinically-adjacent projection is that an **appointment exists**, with its type, time, **branch**, doctor **name** and **specialty**.
> 3. **(c) Only identifier *types* may be persisted.** `caller_verification.verified_identifiers` stores **which identifier types** were confirmed (e.g. `["MemberNo","DateOfBirth"]`) — **never the values** the caller recited. Values remain in patient-service, are never copied into the `callcentre` schema, and are never rendered to the agent for read-out: the caller states the value, the agent confirms it.
> 4. **MemberScoped / all branches.** The Call Centre is a central hotline: its bundle sets `RowScope.BranchUnrestricted`, so **no `BSC` predicate applies**. Branch and specialty are **selectors** on search/booking, never restrictions; a cross-branch read is normal, not a denial.
> 5. **Reuse, don't fork.** Appointment writes delegate to the existing emr endpoints and inherit their guarantees (no double-booking, `Idempotency-Key`, `If-Match`); contact writes delegate to patient-service and inherit its one-primary rule and history. The call centre stores the **linkage** (`interaction_id` / `call_ref`), never a second copy of the record.

### 3.7 Policy-administration resources (Phase 19 — see [38-policy-member-administration.md](38-policy-member-administration.md))

Phase 19 adds the benefit spine to the `policy` schema — `payer`, `plan`, `plan_version`, `benefit_rule` (+ `benefit_rule_tier`), `policy_plan`, `member_group`, `enrollment`, `note`, and the document LINKAGE — plus `network_tier` / `provider_network_assignment` in the `provider` schema.

| Role | payer · plan · plan_version · benefit_rule | policy · policy_plan | member_group · enrollment | note (body) | policy/member document (content) | network_tier · assignment |
|---|---|---|---|---|---|---|
| **Policy Administrator** (§3.22) | C✅ R✅ U🟠(**Draft only** — ADR-0017) | C✅ R✅ U✅ | R✅ *(reads the book its product applies to)* | C✅ R🔒**by class** X✅(`policy:supervise`) | R🔒**by class** | **R✅ only** — prices *at* a tier, never moves a provider between tiers |
| **Beneficiary Management** (§3.4) | R✅ *(the rules it enrols against)* | R✅ | C✅ R✅ U✅ | C✅ R🔒**by class** X🟠(own notes only) | R🔒**by class** | ❌ |
| **Beneficiary-Mgmt Supervisor** (§3.23) | R✅ | R✅ | C✅ R✅ U✅ | C✅ R🔒**by class** X✅(`policy:supervise`) | R🔒**by class** | ❌ |
| **Network Team** (§3.14) | ❌ | ❌ | ❌ | ❌ | ❌ | **C✅ R✅ U✅** *(owns the tier structure — ADR-0019)* |
| Finance | R✅ *(limits and cost-share are money)* | R✅ | R🔒 *(counts + utilization, no clinical field)* | R🔒 **Administrative/Financial only** | R🔒 **Administrative/Financial only** | R✅ |
| Claims Officer / Reviewer | R✅ *(adjudicates against the rules)* | R✅ | R🔒 | R🔒 **Administrative/Financial only** | R🔒 **Administrative/Financial only** | R✅ |
| Call Center | ❌ | R🔒 *(coverage summary only, via §3.6's 360 under `CVP`)* | R🔒**CVP** | R🔒 **Administrative only** | ❌ | ❌ |
| Reception | ❌ | R🔒 *(eligibility verdict card — 07 FR-ELG-003)* | R🔒 | ❌ | ❌ | ❌ |
| Doctors / Nurses / Labs / Imaging / Pharmacies | ❌ | R🔒 *(entitlement as it gates their own act)* | ❌ | ❌ | ❌ | ❌ |
| Medical Approval / Medical Director | R✅ *(the rule being adjudicated)* | R✅ | R🔒 | R🔒 **incl. Clinical** | R🔒 **incl. Clinical** | R✅ |
| Case Managers | R✅ | R✅ | R🔒ASG | R🔒**by class**, ASG | R🔒**by class**, ASG | ❌ |
| Org Admin / Super Admin | C✅ R✅ U✅ | C✅ R✅ U✅ | C✅ R✅ U✅ | R🧨 X✅ | R🧨 | C✅ R✅ U✅ |
| Reporting / analytics | R🔒 **aggregate only** (19.6b read model — no note body, no document, no name) | R🔒 aggregate | R🔒 aggregate + an **audited** id-only drill-down | ❌ | ❌ | R🔒 aggregate |
| DPO / compliance reviewer | R🔒 | R🔒 | R🔒 | R🔒(register: existence, class, author, date — not the body) | R🔒(register) | R🔒 |
| *audit service* | — | — | — | — | — | — |

> **Hard-rule check (must always hold) — policy administration:**
> 1. **(a) Finance and the Call Centre never receive a `Clinical` or `Restricted` note body — and never a `Clinical`/`Identity`/`Restricted` document's content.** The projection withholds by **CLASS**, not by role list, so a note written today is still correctly withheld from a role invented next year. The note's **existence, type, author and date remain visible** with a stated reason: concealing the note entirely would let a Finance reader conclude nothing was recorded (ADR-0018/0021).
> 2. **(b) Notes are append-only.** No role, including Super Admin, has a `U` on `note.body`. A withdrawal is a **cancellation** with a mandatory reason, and the cancelled note stays fully visible, struck through, with its canceller and reason. A correction is a **new note** that supersedes the old one.
> 3. **(c) Payer scope is a restriction, not an entitlement.** A user with no payer assignment is payer-**unrestricted**; a user with assignments sees only those payers' policies, groups, members and aggregates — and **never an unattributed row** (`payer_id IS NULL`). Resolution **fails closed**: when the directory cannot be reached the caller is restricted to nothing, because payer scope's empty set means *unrestricted* and an outage must never widen access (ADR-0024).
> 4. **(d) An Active plan version is immutable.** Editing is Draft-only, enforced by a database trigger and not merely by the service; changing a live plan is `amend → new Draft → activate` (ADR-0017). The read-only affordance in the UI comes from the server's `editable` flag, never re-derived from the status.
> 5. **(e) Tier authorship and tier pricing are different authorities.** Policy administration sets cost-share **at** a tier; the Network Team decides **which** tier a provider sits in, effective-dated, and cost-share resolves per tier **at the service date** (ADR-0019). No role holds both.

---

## 3b. Patient profile — the role × section matrix (Phase 20, HARD RULE)

The unified patient profile ([39 §4](39-patient-profile.md)) deliberately aggregates every zone onto one
screen, so **its section matrix is a hard rule of this document, not a UI concern.** `V` = visible ·
`R` = restricted (existence only, with a reason and where applicable a request-access action) · `—` = **not
returned at all** (the key is absent from the JSON, and the owning service is never called).

| Role | 1 Hdr | 2 Alert | 3 Cov | 4 PMH | 5 Enc | 6 Inv | 7 Rx | 8 Auth | 9 Ref | 10 Doc | 11 Note | 12 Fin | 13 Case | 14 Time | 15 Calls |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **Reception** | V | V | V | — | V(meta) | — | — | V(status) | V | R | V(admin) | — | — | V(admin) | V(operational) |
| **Call Centre** | V | V | V | — | V(meta) | — | — | V(status) | V | R | V(admin) | — | — | V(admin) | V (full) |
| **Doctor/Nurse** (treating) | V | V | V | V | V | V | V | V | V | V | V | — | V | V | V(operational) |
| **Doctor** (non-treating) | V | V | R | R | R | R | R | R | R | R | R | — | — | R | R |
| **Lab / Imaging** | V(min) | V(allergy) | — | — | — | V(own orders) | — | — | — | — | — | — | — | — | — |
| **Pharmacy** | V(min) | V(allergy) | V(pharmacy limit) | — | — | — | V(own Rx) | — | — | — | — | — | — | — | — |
| **Medical Approval** | V | V | V | V | V | V\* | V | V | V | V | V | — | V | V | V(operational) |
| **Medical Director** | V | V | V | V | V | V\* | V | V | V | V | V | V(summary) | V | V | V (full) |
| **Case Manager** (assigned) | V | V | V | V(summary) | V | R | R | V | V | V(admin) | V | — | V | V | V (full) |
| **Finance / Claims** | V(min) | — | V(amounts) | — | V(meta) | — | — | V(cost) | — | R | V(fin) | V | — | V(fin) | V(meta) |
| **Beneficiary Mgmt** | V | V | V | R | V(meta) | — | — | V(status) | V | V(admin) | V | — | — | V(admin) | V (full) |
| **Org/Super Admin** | V(min) | — | — | — | — | — | — | — | — | — | — | — | — | V(access) | — |

\* Sensitive results stay **existence-only even for the approval team and the medical director** until a
[37 §6](37-branch-scoping-and-clinical-sensitivity.md) grant exists. The profile is not a shortcut around that
gate.

Three properties make this enforceable rather than aspirational:

1. **Projection is server-side.** A withheld field is ABSENT from the JSON — never hidden with CSS, never
   present-but-unrendered. Proven by reflection tests over the serialized payload for every role.
2. **Composition runs under the caller's own token**, so each owning service applies its own authorization to
   the call. The section matrix is a second, independent layer; neither is sufficient alone.
3. **It is an intersection, never a union.** Treating relationship, provider ownership, branch scope, payer
   scope, call-centre verification and sensitive-result grants all still bind.

**The photo has its own, narrower list.** Reception, call centre, treating clinicians and beneficiary
management only. Finance, claims, labs, pharmacies and platform admins receive a header with **no photo field**
— it is identity-sensitive, biometric-adjacent data for a refugee population ([39 §5](39-patient-profile.md)),
and it is consent-gated, short-TTL signed, audited on every retrieval, and excluded from exports.

**Call history projects at three levels, not two.** Full / Operational / Meta ([39 §5b](39-patient-profile.md)):
Meta (finance/claims) carries **no summary text**; Operational carries the summary but no verification detail
and no agent notes; only Full sees verification detail. The agent's `notes` column is **never** promoted to any
other role at any level.

---

## 4. Field-level rules for sensitive fields

Even when a role may Read an object, individual fields are governed independently. `visible` = rendered; `masked` = shown redacted/tokenized (e.g., `••••` or coarse category); `derived` = only a computed safety flag, never the raw value; `denied` = field stripped server-side before response.

| Role \ Field | `diagnosis` | `emr_note` | `prescription` | `lab_result` | `imaging_result` | `financials` | `pii` | `refugee_ref` |
|---|---|---|---|---|---|---|---|---|
| Beneficiary Mgmt | denied | denied | denied | denied | denied | denied | visible | visible |
| **Reception** | denied | denied | denied | denied | denied | denied | masked (min) | masked |
| **Call Center** | **denied** | **denied** | **denied** | **denied** | **denied** | masked (balance + remaining limits)🟠CVP | visible(verify: name/contact — identifier **values** never displayed)🟠CVP | masked |
| **Call Centre Supervisor** | **denied** | **denied** | **denied** | **denied** | **denied** | masked (balance + remaining limits)🟠CVP | visible🟠CVP | masked |
| **Doctors** | visible🟠TR | visible🟠TR | visible🟠TR | visible🟠TR | visible🟠TR | denied | visible🟠TR | masked |
| Nurses | visible(problem)🟠 | visible🟠 | masked(admin) | visible🟠 | visible🟠 | denied | visible🟠 | denied |
| **Labs** | masked→indication🟠 | denied | **denied** | visible(own)🟠 | denied | denied | masked | denied |
| **Imaging** | masked→indication🟠 | denied | **denied** | denied | visible(own)🟠 | denied | masked | denied |
| **Pharmacies** | denied | denied | visible🟠 | **denied**→derived(safety flag) | **denied** | masked(copay) | masked | denied |
| **Medical Approval** | visible🟠PUR | visible🟠PUR | visible🟠PUR | visible🟠PUR | visible🟠PUR | masked | visible🟠PUR | masked |
| Medical Director | visible🟠PUR | visible🟠PUR | visible🟠PUR | visible🟠PUR | visible🟠PUR | visible(cost) | visible🟠PUR | masked |
| Case Managers | visible(coord)🟠ASG | masked(summary)🟠 | masked🟠 | masked🟠 | masked🟠 | masked🟠 | visible🟠ASG | masked |
| **Finance** | **denied** | denied | denied | denied | denied | visible | masked(min) | denied |
| **Claims Officer** | **denied** | **denied** | denied | **denied**→existence-only | **denied**→existence-only | visible | masked(min) | denied |
| **Claims Reviewer** | **denied** | **denied** | denied | **denied**→existence-only | **denied**→existence-only | visible | masked(min) | denied |
| Provider Admin | denied | denied | denied | denied | denied | masked(own) | denied | denied |
| Network Team | denied | denied | denied | denied | denied | visible(rates) | denied | denied |
| Org Admin | denied | denied | denied | denied | denied | denied | masked(dir) | denied |
| Super Admin | 🧨 | 🧨 | 🧨 | 🧨 | 🧨 | 🧨 | 🧨 | 🧨 |

**Field-level rules — branch, practitioner & sensitivity fields (Phase 14).** Same vocabulary (`visible` / `masked` / `derived` / `denied`); `existence-only` means the `existence+` projection of §3.5.2 and nothing else.

| Role \ Field | `sensitive_result` (value/report) | `sensitive_category` | `release.justification` | `active_branch` / `branch_id` | `practitioner.license_no` |
|---|---|---|---|---|---|
| **Authoring / ordering doctor** | **visible**🟠TR | visible | visible (own decisions) | visible🟠BSC (own permitted set) | visible (self) |
| Other Doctors / Nurses | **denied → existence-only** (visible🟠SGA) | visible | visible (own request only) | visible🟠BSC | masked |
| **Medical Approval** | **denied → existence-only** (visible🟠SGA) | visible🟠PUR | visible (own request only) | visible (all branches) | masked |
| **Medical Director** | **denied → existence-only** (visible🟠SGA) | visible | visible (as decider) | visible (all branches) | visible |
| Case Managers | **denied → existence-only** (visible🟠SGA) | visible🟠ASG | visible (own request only) | visible (all branches) | masked |
| Reception / Appointment Coordinator | **denied** | denied | denied | visible🟠BSC (own permitted set) | denied |
| **Branch/Clinic Manager** | **denied** | denied | denied | visible🟠BSC (assigned branches) | masked (validity flag only) |
| Finance / Claims Officer / Claims Reviewer | **denied** — existence-only, **no grant ever widens this** | **denied** | denied | visible (reporting dimension) | denied |
| Labs / Imaging (performing provider) | visible🟠(PO+OST) **for their own authored result only** | visible (own order) | denied | — *(branch not exposed to external providers beyond the ordering site name)* | denied |
| Pharmacies | **denied** | denied | denied | — | denied |
| Network Team | denied | denied | denied | visible (all branches) | visible (credentialing) |
| Org Admin | denied | denied | denied | visible (all branches) | masked |
| DPO / compliance reviewer | **denied** (governs the register, not the content) | visible | visible | visible | masked |
| Reporting / analytics | **denied** | aggregated/de-identified only | denied | visible (dimension) | denied |
| Super Admin | 🧨 (loud break-glass) | 🧨 | 🔒 | visible | 🧨 |

**Field-level rules — call-centre fields (Phase 15).** Same vocabulary (`visible` / `masked` / `derived` / `denied`); every `visible` cell for a call-centre role is *additionally* conditioned on `CVP`.

| Role \ Field | `identifier_value` (the value recited by the caller) | `verified_identifier_types` | `call_interaction.notes` | appointment `branch` + `doctor` + `specialty` | clinical field set (`diagnosis`, `emr_note`, `lab_result`, `imaging_result`, `prescription`, examination detail) |
|---|---|---|---|---|---|
| **Call Center** | **denied — never persisted, never rendered** | visible (own interaction) | visible (own calls) | visible (branch name + doctor name + specialty only) | **denied** |
| **Call Centre Supervisor** | **denied — never persisted, never rendered** | visible (team) | visible (team) | visible | **denied** |
| Case Managers | denied | masked🟠ASG | masked🟠ASG (reason + outcome, not the body) | visible🟠ASG | *(per §4 above)* |
| Reception / Appointment Coordinator / Branch Manager | denied | denied | denied | visible🟠BSC (own branch) | denied |
| DPO / compliance reviewer | denied | visible (register) | masked (oversight of disclosure, not the conversation) | visible | denied |
| Reporting / analytics | denied | aggregated only (failure rate) | **denied** | aggregated only | **denied** |

**Key field-level encodings of the hard rules:**
- `Finance.diagnosis = denied` — claims are adjudicated on **billing/service codes + amounts**, never the diagnosis narrative. A procedure code that could reveal condition is exposed only at the minimum granularity needed to price/adjudicate.
- `Labs.prescription = denied`, `Imaging.prescription = denied` — the medication list is stripped from any order payload sent to labs/imaging.
- `Pharmacies.lab_result = denied → derived`, `Pharmacies.imaging_result = denied` — pharmacies receive a **derived safety flag** (e.g., "renal-adjust: yes", "interaction: none") computed server-side from results, never the raw result values.
- `Reception.*clinical = denied` — the entire clinical field set is stripped; Reception receives identity+appointment+eligibility-verdict only.
- `Doctors.* = visible🟠TR` — visible **only** while the treating relationship is active.
- `ClaimsOfficer.diagnosis = denied`, `ClaimsOfficer.emr_note = denied` — **HARD RULE.** Claims are adjudicated on **service code + quantity + date + provider + authorization + amount**, never on the clinical reason. Identical for `ClaimsReviewer`; seniority adds dual-control authority, never clinical read.
- `ClaimsOfficer.lab_result = denied → existence-only`, `ClaimsOfficer.imaging_result = denied → existence-only` — **HARD RULE.** The claims projection exposes `{ exists: true, resulted_at, document_ref, document_type }` and **strips** `value`, `unit`, `reference_range`, `abnormal_flag`, `interpretation`, `report_body`, and any DICOM/report content. This is exactly enough to prove *the service was rendered* and nothing more. `claim_document` inherits the same rule: a clinical attachment is a **reference** to a claims role, not a readable body.
- `Provider.* (claims) = 🟠PO` — a provider-side principal's claim/batch/document/settlement reads are filtered by `provider_ownership`; there is no cross-provider claims read and no bulk export of another payee's data.
- `Beneficiary / CaseManager.claims = self / ASG only` — reimbursement scope only; a member sees their own request, decision outcome and reason code, never a provider claim, batch or settlement advice.
- `*.sensitive_result = denied → existence-only` for **every** role except the authoring/ordering doctor (and the beneficiary) — **HARD RULE.** The server projects `{ sensitive: true, sensitive_category, resulted_at, status, ordering_branch, marker: "RESTRICTED" }` and **strips** `value`, `unit`, `reference_range`, `abnormal_flag`, `interpretation`, `report_body`, narrative and any document content. `MedicalApproval.sensitive_result = denied` **overrides** `MedicalApproval.* = visible🟠PUR` above: purpose-binding does not defeat the sensitivity gate. Release is only via `SGA`, and `Finance/Claims.sensitive_result` stays `denied` even then.
- `release.justification` is itself sensitive — it is written by a clinician and may carry clinical context. It is visible to the **requester** and the **decider** (and to the DPO for oversight), never to unrelated roles, and it is never echoed into notifications ([FR-NOT-003](07-functional-requirements.md)).
- `active_branch / branch_id = visible🟠BSC` for BranchScoped roles — a user may see **which** branches they are permitted to work in and which is active, never the roster or the worklists of a branch they are not assigned to.
- `CallCentre.diagnosis / emr_note / lab_result / imaging_result / prescription / examination_detail = denied` — **HARD RULE.** The call-centre projection is an **allow-list**: identity (member no., display name, age band, member status), eligibility verdict + coverage categories + **remaining limits**, contacts + preferred channel, appointments (id, type, status, `scheduled_start`, branch name, doctor name, specialty), open referrals (`REF-*`) and follow-ups due — and nothing else. A new clinical field is invisible to the call centre by default, and **no query manipulation may widen it**; the projection is applied server-side.
- `CallCentre.* = 🟠CVP` — **HARD RULE.** Every one of those `visible`/`masked` cells is conditioned on a **passed caller verification bound to the current interaction and beneficiary** (§5 `CVP`). Before a pass, the only projection is `{ matchCount, beneficiaryId, displayName, challengeableIdentifierTypes[] }`; the 360, contacts, coverage and appointments are **absent from the payload**, not merely hidden in the UI.
- `CallCentre.identifier_value = denied` — **HARD RULE.** The `callcentre` schema stores **only which identifier types** were confirmed (`caller_verification.verified_identifiers`), never the values; and the agent's screen never displays a stored identifier value to be read out to the caller.
- `practitioner.license_no / license_expiry = visible` only to the Network Team (credentialing) and the practitioner themselves; other roles get a **derived validity flag**, never the number.

**Field-level projection for claims (canonical allow-list).** The claims projection is an **allow-list**, not a deny-list — new clinical fields are invisible to claims by default:

| Projected to claims roles | Never projected to claims roles |
|---|---|
| `service_code` (+ code system), `service_description`, `service_date`, `quantity` | `diagnosis`, `diagnosis_code`, `problem_list` |
| `provider_id`, `provider_location_id`, `contract_id`, `agreed_price` | `emr_note`, `clinical_note`, `soap.*`, `indication`, `chief_complaint` |
| `authorization_id`, `auth_status`, `auth_scope`, `auth_valid_to` | `lab_result.value/unit/range/flag/interpretation` |
| `fulfillment_ref`, `dispense_event_ref`, `fulfilled_at` | `imaging_result.value/report_body/DICOM` |
| `billed_amount`, `allowed_amount`, `adjustment_amount`, `net_payable`, `currency` | `prescription` clinical detail (indication, prescriber notes) |
| `*_result.exists`, `*_result.resulted_at`, `*_result.document_ref` | any free-text authored by a clinician |
| member `pii` at minimum granularity (member no., name, DOB) | `refugee_ref` |

---

## 5. ABAC attributes & conditions

Attributes are asserted by trusted sources: **token claims** (Keycloak), **resource attributes** (from the owning service), and **environment** (device, network, time). The policy engine evaluates them.

| Code | Condition | Attributes evaluated | Source | Applies to |
|---|---|---|---|---|
| `TR` | Treating relationship active | `subject.id ∈ resource.care_team` AND `encounter.status = open` (or within continuity window) | emr/orders service | Doctors, Nurses |
| `PO` | Provider ownership | `subject.provider_id = resource.provider_id` | provider/orders service | Labs, Imaging, Pharmacies, Provider Admin, Reception |
| `TEN` | Tenant match | `subject.tenant_id = resource.tenant_id` | all services (RLS) | All roles |
| `ASG` | Assignment | `subject.id ∈ resource.assigned_users` (case load / panel) | patient/approvals | Case Managers, Nurses |
| `OST` | Order status gate | `resource.status ∈ {routed, accepted, in_progress}` and routed_to = subject.provider | orders service | Labs, Imaging, Pharmacies |
| `PUR` | Purpose binding | `request.purpose = "utilization_review"` AND active `approval_case` links resource | approvals + policy engine | Medical Approval, Medical Director |
| `SOD` | Segregation-of-duties clear | `subject.id ≠ resource.originator_id` (and no conflicting role held) | approvals/finance/identity | Approval, Director, Finance, Beneficiary Mgmt |
| `BG` | Break-glass active | active, time-boxed `break_glass_grant` for `(subject, resource)` with reason + dual approval | identity + audit | Super Admin, emergency clinicians |
| `CNA` | **Claims originator ≠ adjudicator** (SoD) | `subject.id ∉ {resource.created_by, resource.submitted_by, resource.requested_by}` for the claim **and** for the parent claim/reimbursement of a line | claims service | Claims Officer, Claims Reviewer |
| `NPA` | **Not provider-affiliated** | `subject.provider_id` is null/absent **AND** `subject.id ∉ resource.provider.affiliated_user_ids` **AND** `subject.provider_group_id ≠ resource.provider_group_id` | identity + provider service | Claims Officer, Claims Reviewer |
| `DCT` | **Dual control above threshold** | `resource.amount > policy.dual_control_threshold ⇒ approver.id ≠ resource.recorded_by AND approver.role = claims_reviewer AND CNA AND NPA` | claims service + policy config | Claims Reviewer (override, high-value adjustment, negative net payable) |
| `BOS` | **Batch open, single membership** | `batch.status ∈ {Open, UnderReview}` **AND** the claim has no other batch membership where `batch_status ∈ {Open, UnderReview}`; removal from `UnderReview` requires `reason != ""` | claims service (unique partial index) | Claims Officer, Claims Reviewer |
| **`BSC`** | **Branch scope** | `resource.branch_id ∈ subject.permitted_branch_ids` **AND** (`subject.scope_mode != "BranchScoped"` **OR** `resource.branch_id == subject.active_branch_id`). `permitted_branch_ids` = the user's `Home` ∪ `Additional` assignments filtered to `status='Active'` and `valid_from ≤ now < valid_to`; `active_branch_id` is taken from the `X-Active-Branch` header **only after** it is validated against that set (absent ⇒ Home; outside the set ⇒ `403` + audited `BranchScopeDenied`). Never trust the header. | identity (assignments, active-branch claim) + the owning service (resource `branch_id`) | **BranchScoped:** Reception, Appointment Coordinator, Nurses, Doctors *(operational lists)*, Branch/Clinic Manager. **MemberScoped** bundles set `BranchUnrestricted`; **ProviderScoped** roles use `PO` instead |
| **`SGA`** | **Sensitive grant active** | `∃ report_access_grant g : g.grantee_user_id == subject.id AND g.result_ref == resource.result_ref AND g.revoked_at IS NULL AND now() < g.expires_at AND g.request.status == 'Approved'`. The grant is **single-result and non-transferable** — no role-, team- or case-level grant exists. Every hit **must** emit a `SensitiveResultReadUnderGrant` audit event carrying `grant_id`, `purpose_code` and actor. | orders service (`report_access_grant`) + audit | Any principal reading a result where `sensitivity_level != 'Standard'` **and** who is not the authoring/ordering doctor. **Never** satisfiable for Finance/Claims roles |

| **`CVP`** | **Caller verification passed** | `∃ caller_verification v : v.interaction_id == request.aux.call.interaction_id AND v.beneficiary_id == resource.beneficiary_id AND v.result == 'Passed' AND count(v.verified_identifiers) >= policy.min_identifier_types` (**default 2**, configurable) **AND** `interaction.status == 'Open'`. The verification is **single-interaction and single-beneficiary**, **expires when the interaction closes**, and is **never inherited** from an earlier call. Absent/failed ⇒ **`403` + audited `CallerVerificationRequired`**; the failed attempt itself is persisted **and** audited. Only the identifier **types** are stored — never the values. | callcentre service (`caller_verification`, `call_interaction`) + audit | **Call Center, Call Centre Supervisor** — every member-360 read and every appointment (book/reschedule/cancel) or contact mutation performed from a call |

**Environmental modifiers (Zero Trust):** every decision may additionally require `device.compliant = true`, `network.ip ∈ allowlist` (for admin/finance), `auth.mfa = true`, and `auth.acr ≥ step_up` for T3/T4 or Export. These are combined by the gateway/policy engine (see [Security Model §4](18-security-model.md)).

---

## 6. Example policy rules (pseudo-Rego / Cerbos)

These illustrate how the matrix compiles into policy. The deployed bundles are the tested, versioned artifacts; these are representative.

### 6.1 Rego — Doctors read EMR only for treated patients (`TR`)

```rego
package hbmp.emr

default allow = false

allow {
    input.action == "read"
    input.resource.type == "emr_record"
    input.subject.role == "doctor"
    input.subject.tenant_id == input.resource.tenant_id          # TEN
    some m
    input.resource.care_team[m] == input.subject.id              # TR membership
    input.resource.encounter.status == "open"                    # TR active
    input.env.mfa == true
}

# continuity-of-care fallback within retention window
allow {
    input.action == "read"
    input.resource.type == "emr_record"
    input.subject.role == "doctor"
    input.resource.care_team[_] == input.subject.id
    time.now_ns() < input.resource.continuity_window_end_ns
    input.env.mfa == true
}
```

### 6.2 Rego — Finance may read claim but diagnosis field is stripped

```rego
package hbmp.claim

allow_read_claim {
    input.subject.role == "finance"
    input.action == "read"
    input.resource.type == "claim"
    input.subject.tenant_id == input.resource.tenant_id
}

# field-level: fields Finance may NOT receive
denied_fields := {"diagnosis", "emr_note", "prescription",
                  "lab_result", "imaging_result", "refugee_ref"} {
    input.subject.role == "finance"
}
# The service removes denied_fields from the response projection.
```

### 6.3 Rego — Labs see order + indication but never prescriptions

```rego
package hbmp.order

allow {
    input.action == "read"
    input.resource.type == "order"
    input.resource.order_class == "lab"
    input.subject.role == "lab"
    input.subject.provider_id == input.resource.routed_to        # PO
    input.resource.status == "routed"                            # OST
}

denied_fields := {"prescription", "medication_list", "unrelated_history"} {
    input.subject.role == "lab"
}
```

### 6.4 Rego — Pharmacy gets derived safety flag, not raw lab results

```rego
package hbmp.pharmacy

allow_read_rx {
    input.subject.role == "pharmacy"
    input.action == "read"
    input.resource.type == "prescription"
    input.subject.provider_id == input.resource.routed_to        # PO
    input.resource.status == "routed"                            # OST
}

# raw investigation results are always denied to pharmacy;
# only server-computed safety_flags are projected.
denied_fields := {"lab_result", "imaging_result", "raw_result_value"} {
    input.subject.role == "pharmacy"
}
```

### 6.5 Cerbos — Medical Approval reads EMR under purpose binding + SoD

```yaml
apiVersion: api.cerbos.dev/v1
resourcePolicy:
  resource: "emr_record"
  version: "default"
  rules:
    - actions: ["read"]
      effect: EFFECT_ALLOW
      roles: ["medical_approval", "medical_director"]
      condition:
        match:
          all:
            of:
              - expr: request.aux.jwt.tenant_id == resource.attr.tenant_id      # TEN
              - expr: request.aux.purpose == "utilization_review"               # PUR
              - expr: resource.attr.linked_approval_case != ""                  # case link
              - expr: request.principal.attr.mfa == true
    - actions: ["approve"]
      effect: EFFECT_DENY
      roles: ["medical_approval", "medical_director"]
      condition:
        match:
          expr: request.principal.id == resource.attr.originator_id             # SOD: no self-approval
```

### 6.6 Cerbos — Break-glass override with mandatory audit

```yaml
apiVersion: api.cerbos.dev/v1
resourcePolicy:
  resource: "emr_record"
  version: "default"
  rules:
    - actions: ["read"]
      effect: EFFECT_ALLOW
      roles: ["super_admin", "emergency_clinician"]
      condition:
        match:
          all:
            of:
              - expr: request.aux.break_glass.active == true                    # BG
              - expr: request.aux.break_glass.approved_by != request.principal.id # dual control
              - expr: timestamp(request.aux.break_glass.expires_at) > now()     # time-boxed
              - expr: request.principal.attr.mfa_hardware == true
      # NOTE: gateway MUST emit break_glass.read audit event on every hit.
```

### 6.7 Cerbos — Segregation of duties on payment release

```yaml
apiVersion: api.cerbos.dev/v1
resourcePolicy:
  resource: "payment"
  version: "default"
  rules:
    - actions: ["approve"]         # release
      effect: EFFECT_ALLOW
      roles: ["finance"]
      condition:
        match:
          all:
            of:
              - expr: request.principal.attr.can_release == true
              - expr: request.principal.id != resource.attr.initiated_by        # SOD
              - expr: request.principal.attr.ip_allowlisted == true
              - expr: request.principal.id != resource.attr.claim_adjudicated_by # adjudication ≠ settlement release
```

### 6.8 Rego — Claims Officer decides a line: SoD + not-provider-affiliated, clinical fields stripped

```rego
package hbmp.claims

default allow = false

# DC — record a line-level adjudication decision
allow {
    input.action == "decide"
    input.resource.type == "claim_line"
    input.subject.role == "claims_officer"
    input.subject.tenant_id == input.resource.tenant_id                  # TEN
    input.subject.id != input.resource.claim.created_by                  # CNA
    input.subject.id != input.resource.claim.submitted_by                # CNA
    not provider_affiliated                                              # NPA
    input.env.mfa == true
}

provider_affiliated {
    input.subject.provider_id == input.resource.claim.provider_id
}
provider_affiliated {
    input.subject.provider_group_id == input.resource.claim.provider_group_id
}
provider_affiliated {
    input.resource.claim.provider.affiliated_user_ids[_] == input.subject.id
}

# HARD RULE — clinical content is never projected to claims roles.
# Allow-list projection; anything not listed is stripped server-side.
allowed_fields := {"service_code", "service_description", "service_date", "quantity",
                   "provider_id", "provider_location_id", "contract_id", "agreed_price",
                   "authorization_id", "auth_status", "auth_scope",
                   "fulfillment_ref", "dispense_event_ref",
                   "billed_amount", "allowed_amount", "adjustment_amount", "currency",
                   "result_exists", "resulted_at", "result_document_ref"} {
    input.subject.role in {"claims_officer", "claims_reviewer"}
}

denied_fields := {"diagnosis", "diagnosis_code", "emr_note", "clinical_note", "indication",
                  "lab_result", "imaging_result", "raw_result_value", "result_value",
                  "reference_range", "abnormal_flag", "interpretation", "report_body",
                  "prescription_clinical_detail", "refugee_ref"} {
    input.subject.role in {"claims_officer", "claims_reviewer"}
}
```

### 6.9 Rego — provider sees only its own claims/batches (`PO`); member sees only own reimbursement

```rego
package hbmp.claims.scope

allow {
    input.action == "read"
    input.resource.type in {"claim", "claim_line", "claim_batch",
                            "claim_document", "settlement_advice"}
    input.subject.role in {"provider_admin", "provider_user"}
    input.subject.provider_id == input.resource.provider_id              # PO
    input.subject.tenant_id == input.resource.tenant_id                  # TEN
}

# a provider may never decide, adjust, batch or void
deny {
    input.action in {"decide", "adjust", "batch", "void"}
    input.subject.role in {"provider_admin", "provider_user"}
}

# beneficiary / case manager: reimbursement only, own record only
allow {
    input.action in {"create", "read"}
    input.resource.type in {"reimbursement_request", "claim_document"}
    input.subject.role == "beneficiary"
    input.resource.beneficiary_id == input.subject.beneficiary_id        # self
}

# update only while still editable (pre-submit)
allow {
    input.action == "update"
    input.resource.type == "reimbursement_request"
    input.subject.role == "beneficiary"
    input.resource.beneficiary_id == input.subject.beneficiary_id        # self
    input.resource.status == "Draft"
}

allow {
    input.action in {"create", "read", "update"}
    input.resource.type in {"reimbursement_request", "claim_document"}
    input.subject.role == "case_manager"
    input.resource.beneficiary_id == input.subject.assigned_beneficiaries[_]   # ASG
}
```

### 6.10 Cerbos — dual control above threshold (`DCT`) + single open batch (`BOS`)

```yaml
apiVersion: api.cerbos.dev/v1
resourcePolicy:
  resource: "claim_adjustment"
  version: "default"
  rules:
    - actions: ["approve"]            # dual-control approval of an override / high-value adjustment
      effect: EFFECT_ALLOW
      roles: ["claims_reviewer"]
      condition:
        match:
          all:
            of:
              - expr: request.aux.jwt.tenant_id == resource.attr.tenant_id            # TEN
              - expr: request.principal.id != resource.attr.recorded_by               # DCT dual control
              - expr: request.principal.id != resource.attr.claim_created_by          # CNA
              - expr: request.principal.id != resource.attr.claim_submitted_by        # CNA
              - expr: request.principal.attr.provider_id != resource.attr.provider_id # NPA
              - expr: request.principal.attr.mfa == true
    - actions: ["approve"]
      effect: EFFECT_DENY
      roles: ["claims_officer"]
      condition:
        match:
          expr: resource.attr.amount > resource.attr.dual_control_threshold  # must escalate to reviewer
---
apiVersion: api.cerbos.dev/v1
resourcePolicy:
  resource: "claim_batch"
  version: "default"
  rules:
    - actions: ["batch"]              # add a claim to a batch
      effect: EFFECT_ALLOW
      roles: ["claims_officer", "claims_reviewer"]
      condition:
        match:
          all:
            of:
              - expr: resource.attr.status in ["Open", "UnderReview"]                 # BOS
              - expr: request.aux.claim.open_batch_count == 0                         # BOS single membership
              - expr: request.aux.jwt.tenant_id == resource.attr.tenant_id
      # DB backstop: unique partial index on claim_id WHERE batch_status IN (Open, UnderReview)
    - actions: ["batch"]              # remove from a batch already under review
      effect: EFFECT_DENY
      roles: ["claims_officer"]
      condition:
        match:
          all:
            of:
              - expr: resource.attr.status == "UnderReview"
              - expr: request.aux.removal_reason == ""                                # reason mandatory
```

### 6.11 Rego — branch scope (`BSC`): a BranchScoped role reaches only the active branch

```rego
package hbmp.branch

default allow = false

branch_scoped_roles := {"reception", "appointment_coordinator", "nurse",
                        "doctor", "branch_manager"}

# the permitted set is Home ∪ Additional, active and in-window (asserted by identity)
permitted { input.subject.permitted_branch_ids[_] == input.subject.active_branch_id }

allow {
    input.action == "read"
    input.resource.type in {"appointment", "appointment_slot", "queue_entry",
                            "encounter", "investigation_order", "waitlist_entry"}
    input.subject.role in branch_scoped_roles
    input.subject.tenant_id == input.resource.tenant_id            # TEN
    permitted                                                      # header validated server-side
    input.resource.branch_id == input.subject.active_branch_id     # BSC (narrowing)
    treating_or_site_relationship                                  # BSC never replaces TR/ASG
}

# MemberScoped bundles (approvals, finance, claims, case mgmt, reporting) omit the
# branch predicate entirely: RowScope.BranchUnrestricted = true.
allow {
    input.action == "read"
    input.subject.scope_mode == "MemberScoped"
    input.subject.tenant_id == input.resource.tenant_id
    other_conditions_hold
}

# a cross-branch attempt is an explicit DENY (403 + audit), never an empty result set
deny[reason] {
    input.subject.role in branch_scoped_roles
    input.resource.branch_id != input.subject.active_branch_id
    reason := "BranchScopeDenied"
}

# an active branch outside the permitted set is rejected before any resource lookup
deny[reason] {
    not permitted
    reason := "BranchScopeDenied"
}
```

### 6.12 Cerbos — sensitive result: default-deny, author-only, released only by an active grant

```yaml
apiVersion: api.cerbos.dev/v1
resourcePolicy:
  resource: "investigation_result"
  version: "default"
  rules:
    # existence metadata is always available to anyone who may see the order at all
    - actions: ["read:existence"]          # category, resulted_at, status, branch, RESTRICTED marker
      effect: EFFECT_ALLOW
      roles: ["doctor", "nurse", "medical_approval", "medical_director",
              "case_manager", "claims_officer", "claims_reviewer", "finance"]
      condition:
        match:
          expr: request.aux.jwt.tenant_id == resource.attr.tenant_id              # TEN

    # HARD RULE — the content of a non-Standard result is denied to everyone by default
    - actions: ["read:value", "read:report_document"]
      effect: EFFECT_DENY
      roles: ["*"]
      condition:
        match:
          expr: resource.attr.sensitivity_level != "Standard"

    # …except the authoring / ordering doctor, with a live treating relationship
    - actions: ["read:value", "read:report_document"]
      effect: EFFECT_ALLOW
      roles: ["doctor"]
      condition:
        match:
          any:
            of:
              - expr: request.principal.id == resource.attr.authored_by
              - expr: request.principal.id == resource.attr.ordered_by
          all:
            of:
              - expr: request.principal.id in resource.attr.care_team             # TR
              - expr: request.principal.attr.mfa == true

    # …or a principal holding an active, unexpired, unrevoked, single-result grant
    - actions: ["read:value", "read:report_document"]
      effect: EFFECT_ALLOW
      roles: ["doctor", "nurse", "medical_approval", "medical_director", "case_manager"]
      condition:
        match:
          all:
            of:
              - expr: request.aux.grant.grantee_user_id == request.principal.id   # SGA non-transferable
              - expr: request.aux.grant.result_ref == resource.attr.result_ref    # SGA single-result
              - expr: request.aux.grant.revoked_at == null
              - expr: timestamp(request.aux.grant.expires_at) > now()             # SGA time-boxed
              - expr: request.aux.grant.purpose_code != ""
              - expr: request.principal.attr.mfa == true
      # NOTE: the service MUST emit SensitiveResultReadUnderGrant (grant_id, purpose_code,
      # actor) on every hit — separately from the ordinary PHI-read audit event.

    # claims/finance are never eligible, grant or no grant
    - actions: ["read:value", "read:report_document"]
      effect: EFFECT_DENY
      roles: ["claims_officer", "claims_reviewer", "finance", "reception",
              "branch_manager", "provider_admin", "pharmacy"]
---
apiVersion: api.cerbos.dev/v1
resourcePolicy:
  resource: "report_access_request"
  version: "default"
  rules:
    - actions: ["create"]
      effect: EFFECT_ALLOW
      roles: ["doctor", "nurse", "medical_approval", "medical_director", "case_manager"]
      condition:
        match:
          all:
            of:
              - expr: request.resource.attr.purpose_code in
                      ["ContinuityOfCare","AuthorizationDecision","ClinicalReview",
                       "Complaint","Legal","Other"]                               # mandatory
              - expr: size(request.resource.attr.justification) > 0               # mandatory
    - actions: ["decide"]
      effect: EFFECT_ALLOW
      roles: ["doctor", "medical_director"]
      condition:
        match:
          all:
            of:
              - expr: request.principal.id != resource.attr.requested_by          # SOD
              - expr: request.principal.id == resource.attr.result_authored_by ||
                      request.principal.attr.role == "medical_director"           # author OR Director
              - expr: request.principal.attr.mfa == true
      # NOTE: a medical_director decision sets decided_by_role=MedicalDirector and
      # MUST raise an additional, high-severity audit event (extra-audited).
```

### 6.13 Rego — call centre: nothing is disclosed before the caller is verified (`CVP`), and no clinical field ever

```rego
package hbmp.callcentre

default allow = false

call_centre_roles := {"call_centre_agent", "call_centre_supervisor"}

# CVP — a Passed verification for THIS interaction and THIS beneficiary, ≥ 2 identifier
# types, while the interaction is still open. Never inherited from an earlier call.
verified {
    v := input.aux.verification
    v.interaction_id == input.aux.call.interaction_id
    v.beneficiary_id == input.resource.beneficiary_id
    v.result == "Passed"
    count(v.verified_identifier_types) >= input.policy.min_identifier_types   # default 2
    input.aux.call.status == "Open"
}

# pre-verification: search only, and only enough to run the challenge
allow {
    input.action == "read"
    input.resource.type == "call_centre_search_result"
    input.subject.role in call_centre_roles
    input.subject.tenant_id == input.resource.tenant_id                       # TEN
}

# member 360 + every appointment/contact action from a call
allow {
    input.action in {"read", "create", "update"}
    input.resource.type in {"member_360", "appointment", "contact", "call_interaction"}
    input.subject.role in call_centre_roles
    input.subject.tenant_id == input.resource.tenant_id                       # TEN
    verified                                                                 # CVP
    input.env.mfa == true
}

# cancelling from a call always carries a coded reason
deny[reason] {
    input.action == "update"
    input.resource.type == "appointment"
    input.request.body.operation == "cancel"
    input.request.body.reason_code == ""
    reason := "CancelReasonRequired"                                          # 422
}

# an unverified (or failed, or expired) interaction is an explicit DENY + audit,
# never a thinned-out 200
deny[reason] {
    input.resource.type in {"member_360", "appointment", "contact"}
    input.subject.role in call_centre_roles
    not verified
    reason := "CallerVerificationRequired"                                    # 403 + audit
}

# MemberScoped: the hotline is never branch-filtered — no BSC predicate applies
branch_unrestricted { input.subject.role in call_centre_roles }

# HARD RULE — clinical content is never projected to the call centre.
# Allow-list projection; anything not listed is stripped server-side.
allowed_fields := {"member_no", "display_name", "age_band", "member_status",
                   "eligibility_verdict", "coverage_category", "remaining_limit",
                   "contact_value", "preferred_channel", "address",
                   "appointment_id", "appointment_type", "appointment_status",
                   "scheduled_start", "branch_name", "doctor_name", "specialty",
                   "referral_ref", "referral_status", "follow_up_due_at"} {
    input.subject.role in call_centre_roles
}

denied_fields := {"diagnosis", "diagnosis_code", "problem_list", "emr_note", "clinical_note",
                  "soap", "indication", "chief_complaint", "examination_detail",
                  "lab_result", "imaging_result", "result_value", "report_body",
                  "prescription", "medication_list", "refugee_ref",
                  "identifier_value"} {                    # values are never stored or shown
    input.subject.role in call_centre_roles
}

# the sensitivity gate is moot here — the call centre never reaches a result at all
deny[reason] {
    input.resource.type in {"investigation_result", "prescription", "emr_record",
                            "clinical_note", "diagnosis"}
    input.subject.role in call_centre_roles
    reason := "CallCentreClinicalDenied"
}
```

---

## 7. Deny-by-default & precedence

1. **Default deny.** Absent an explicit allow, access is denied at every layer.
2. **Field-deny overrides object-allow.** If a role may read an object but a field is `denied`, the field is stripped even on an allowed read.
3. **SoD-deny overrides role-allow.** An action is denied if the subject is conflicted for that specific record, regardless of role.
4. **Break-glass never silently widens.** `BG` allows only what its grant scopes, for its window, and always emits audit; it cannot be used to bypass SoD deny for self-approval.
5. **Environment can only restrict, never expand.** Failing device/MFA/IP checks can turn ✅ into ❌ but never the reverse.
6. **Sensitivity-deny overrides purpose-binding and every role grant.** A non-`Standard` result's `value`/report is denied even to a role whose row says ✅ (Medical Approval under `PUR`, Medical Director, Case Manager). Only `SGA` — an active, unexpired, unrevoked, single-result, non-transferable grant — or authorship lifts it; break-glass lifts it *loudly*.
7. **Branch-scope narrows, never grants.** `BSC` can only remove rows from a result set (and turn a cross-branch request into a `403`). Satisfying `BSC` never substitutes for `TR`, `PO`, `ASG`, `PUR` or the field rules, and no branch assignment widens a field projection.
8. **Verification-deny precedes every call-centre allow.** For a call-centre principal, `CVP` is evaluated as an **explicit deny ahead of the role grant**: absent an interaction-bound, beneficiary-bound `Passed` verification, the member 360 and every appointment/contact action are denied (`403` + audit) whatever §3 grants. It cannot be satisfied by a previous call, by a colleague's verification, by a supervisor's team-wide read, or by break-glass — and satisfying it never widens the field projection: the clinical set stays `denied`.

Precedence order evaluated by the policy engine: `explicit-deny (field/SoD/sensitivity/branch/verification/env)` ▶ `break-glass-scoped-allow` ▶ `grant-scoped-allow (SGA)` ▶ `ABAC-conditional-allow` ▶ `RBAC-allow` ▶ `default-deny`.

---

## 8. Consistency rules with the Role Matrix

Every cell here must agree with [10-role-matrix.md §5–7](10-role-matrix.md). The hard rules are re-verified here in §3.2, §3.4 and §4. A CI check (design-lint) should assert:
- Reception has zero non-❌ cells in the clinical block.
- Finance `diagnosis` field = `denied` and clinical objects = ❌.
- Labs/Imaging `prescription` = `denied`.
- Pharmacies `lab_result`/`imaging_result` = `denied`.
- Doctors clinical reads all carry `TR`.
- Approval/Director clinical reads all carry `PUR`.
- **Claims Officer/Reviewer `diagnosis` = `denied`, `emr_note` = `denied`, `lab_result`/`imaging_result` = `denied → existence-only`**, and the claims projection is an allow-list (§4).
- **Every provider-side claims cell carries `PO`**; no cross-provider claims read exists.
- **Beneficiary/Case Manager claims cells are `self`/`ASG` and reimbursement-only.**
- **`DC` cells all carry `CNA` + `NPA`**; **`A` on `claim_decision`/`claim_adjustment` carries `DCT`**; **`B` carries `BOS`**.
- Claims adjudication and settlement release are never held by the same principal (§6.7, §6.8).
- No resource in §2 exposes an "execute payment" action.
- **Every role in §3.5.1 declares exactly one scope mode**, and it matches [10 §2](10-role-matrix.md); no role is both BranchScoped and `BranchUnrestricted`.
- **Every branch-scoped resource read carries `BSC`**; no policy in a BranchScoped bundle reads a branch-scoped resource without a `branch_id` predicate.
- **A cross-branch read is a deny, not a filter** — the branch fixtures assert `403` + `BranchScopeDenied`, and explicitly assert that the response is **not** an empty `200`.
- **`sensitive_result` = `denied → existence-only` for every role except the authoring/ordering doctor**, including `medical_approval`, `medical_director` and `case_manager`; `finance`/`claims_officer`/`claims_reviewer` are `denied` **with no `SGA` path**.
- **No grant is role-, team- or case-scoped** — every `report_access_grant` cell asserts a single `grantee_user_id` + single `result_ref`, a non-null `expires_at`, and no delegation/transfer action anywhere in §2.
- **Every `report_access_request` create carries a non-empty `purpose_code` + `justification`**, and every `decide` carries `SOD` (requester ≠ decider) with the decider being the authoring doctor **or** a Medical Director.
- **Every allow that depends on `SGA` is paired with a `SensitiveResultReadUnderGrant` audit assertion** — a read-under-grant that does not audit is a failing build.
- **No user may create/update their own `user_branch_assignment`** — the self-grant fixture must deny for every role, including Branch/Clinic Manager and Org Admin acting on themselves.
- **Every call-centre read of the member 360, and every appointment/contact write from a call, carries `CVP`** — no policy in the call-centre bundle reaches a beneficiary resource without an interaction-bound, beneficiary-bound `Passed` verification predicate.
- **The unverified fixture is a deny, not a thin payload** — it must assert `403` + audited `CallerVerificationRequired`, and explicitly assert that the response is **not** a `200` with fields omitted.
- **A verification is single-interaction and single-beneficiary** — fixtures must deny a verification reused across interactions, reused for another beneficiary, or used after the interaction is `Closed`; and a `Passed` result with **fewer than 2 identifier types** must be rejected (`422`).
- **A failed verification is persisted and audited** — a fixture that discards a `Failed` attempt, or records it without an audit event, is a failing build.
- **Call Center / Call Centre Supervisor `diagnosis`, `emr_note`, `lab_result`, `imaging_result`, `prescription` and examination detail = `denied`**, with **no `PUR`, `SGA`, `ASG` or `BG` path** anywhere in the bundle that lifts them; the call-centre projection is an **allow-list** (§4) asserted over the **serialized response**, not just the DTO type.
- **No identifier value is persisted in the `callcentre` schema** — a design-lint/schema assertion must show `caller_verification` carries identifier **types** only, and the frontend fixture must assert no stored identifier value is ever rendered.
- **The Call Centre bundle declares `MemberScoped` / `BranchUnrestricted`** and contains **no `BSC` predicate** — a cross-branch read is normal for this role, and branch/specialty appear only as selectors.
- **Every call-centre disclosure and mutation is audited and correlated by `call_ref`** — search, verification (pass *and* fail), 360 read, book/reschedule/cancel, contact update, interaction open/close.

---

## 9. Cross-references
- Narrative role definitions & SoD table → **[10-role-matrix.md](10-role-matrix.md)**
- Enforcement points (gateway/service/RLS/field), Zero Trust, step-up, break-glass → **[18-security-model.md](18-security-model.md)**
- Audit of every read/write/approve/consume/export → **[19-audit-strategy.md](19-audit-strategy.md)**
- Regulatory mapping of minimization → **[20-compliance-checklist.md](20-compliance-checklist.md)**
- Claims module design (origination, batching, adjudication, adjustments, settlement) → **[36-claims-management.md](36-claims-management.md)**
- Branch model, scope modes (`BSC`), practitioner specialty, sensitivity classification and the release-request workflow (`SGA`) → **[37-branch-scoping-and-clinical-sensitivity.md](37-branch-scoping-and-clinical-sensitivity.md)**

> **Change control:** policy bundles are versioned, peer-reviewed by Security Architect + DPO, tested against a permission-regression suite (including the six hard rules), and deployed via the audited pipeline. No manual policy edits in production.
