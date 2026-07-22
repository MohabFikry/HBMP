# 11 — Permission Matrix (Fine-Grained, Enforceable)

[⬅ Back to Index](00-README-INDEX.md) · [Design Foundations](0A-DESIGN-FOUNDATIONS.md)

**Siblings:** [10-role-matrix.md](10-role-matrix.md) · [18-security-model.md](18-security-model.md) · [19-audit-strategy.md](19-audit-strategy.md) · [20-compliance-checklist.md](20-compliance-checklist.md)

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

**Decision values**

| Symbol | Meaning |
|---|---|
| ✅ | Allowed within role scope (no extra condition beyond scope + tenant) |
| 🟠 | Allowed **only if** ABAC condition holds (see condition code in cell or §5) |
| 🔒 | Allowed but **field-masked/minimized** (object visible, sensitive fields removed) |
| 🧨 | Allowed **only via break-glass** (time-boxed, dual-control, loud audit) |
| ❌ | Denied |
| — | Not applicable to this role/resource |

**ABAC condition codes** (defined fully in §5): `TR` treating-relationship · `PO` provider-ownership · `TEN` tenant-match · `ASG` assignment · `OST` order-status · `PUR` purpose-binding · `SOD` segregation-of-duties clear · `BG` break-glass active.

**Sensitivity fields tracked at field level:** `diagnosis`, `emr_note`, `prescription`, `lab_result`, `imaging_result`, `financials` (amounts/claims), `pii` (identity/registration), `refugee_ref` (UNHCR/registration ID).

---

## 2. Resource catalog

Resources map to microservices (see [0A](0A-DESIGN-FOUNDATIONS.md)). Object-level and field-level rules follow.

| Resource | Owning service | Key sensitive fields |
|---|---|---|
| `beneficiary` | patient | `pii`, `refugee_ref` |
| `household` | patient | `pii` |
| `policy` / `benefit_plan` | policy | `financials` (limits) |
| `eligibility_check` | eligibility | verdict only |
| `emr_record` | emr | `emr_note`, `diagnosis` |
| `clinical_note` | emr | `emr_note`, `diagnosis` |
| `diagnosis` | emr | `diagnosis` |
| `order` (lab/imaging/procedure) | orders | indication, `lab_result`/`imaging_result` |
| `prescription` | orders | `prescription` |
| `lab_result` | orders | `lab_result` |
| `imaging_result` | orders | `imaging_result` |
| `approval_case` | approvals | attached clinical evidence |
| `provider` / `contract` / `catalog` | provider | `financials` (rates) |
| `claim` / `invoice` / `payment` | finance (reporting) | `financials`, service codes |
| `user` / `role_binding` | identity | admin metadata |
| `audit_event` | audit | append-only |
| `document` (reports, DICOM ref) | document | clinical attachments |

---

## 3. Object-level permission matrix

Cells show allowed actions with their decision symbol. Absent actions are denied. **All ✅/🟠 reads are still subject to field-level rules in §4.**

### 3.1 Beneficiary & identity data

| Role | beneficiary | household | policy/plan | eligibility_check |
|---|---|---|---|---|
| Beneficiary Mgmt | C✅ R✅ U✅ D🟠(SOD) | C✅ R✅ U✅ | R✅ U🟠(assign) | R✅ |
| Reception | R🔒(pii min) 🟠TR/ASG | — | R🔒(verdict) | C✅ R🔒 |
| Call Center | R🔒 U🟠(contact) | R🔒 | R🔒(coverage) | C✅ R🔒 |
| Doctors | R🟠TR | R🟠TR | R🔒🟠TR | R🔒 |
| Nurses | R🟠(TR/ASG) | — | R🔒 | R🔒 |
| Labs | R🔒🟠(PO+OST) | — | — | — |
| Imaging | R🔒🟠(PO+OST) | — | — | — |
| Pharmacies | R🔒🟠(PO+OST) | — | R🔒(drug cov)🟠 | R🔒🟠 |
| Medical Approval | R✅ | R✅ | R✅ | R✅ |
| Medical Director | R✅ | R✅ | R✅ | R✅ |
| Case Managers | R🟠ASG | R🟠ASG | R🟠ASG | R🟠ASG |
| Finance | R🔒(pii min) | — | R✅(financial) | R🔒 |
| Provider Admin | ❌ | ❌ | ❌ | ❌ |
| Network Team | ❌ | ❌ | R🔒(contract) | ❌ |
| Org Admin | R🔒(dir)🟠 | ❌ | R🔒(config) | ❌ |
| Super Admin | R🧨 | R🧨 | R🧨 | R🧨 |

### 3.2 Clinical / EMR data — the core minimization zone

| Role | emr_record | clinical_note | diagnosis | prescription | lab_result | imaging_result |
|---|---|---|---|---|---|---|
| Beneficiary Mgmt | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Reception** | **❌** | **❌** | **❌** | **❌** | **❌** | **❌** |
| Call Center | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Doctors** | C🟠TR R🟠TR U🟠TR | C🟠TR R🟠TR U🟠TR | C🟠TR R🟠TR | C🟠TR R🟠TR | R🟠TR | R🟠TR |
| Nurses | R🟠(TR/ASG) U🟠(nursing) | C🟠(nursing) R🟠 | R🟠(problem list) | R🔒(admin only) | R🟠(TR) | R🟠(TR) |
| **Labs** | R🔒(indication)🟠(PO+OST) | ❌ | R🔒(indication only)🟠 | **❌** | C🟠(PO+OST) R🟠 U🟠 | ❌ |
| **Imaging** | R🔒(indication)🟠(PO+OST) | ❌ | R🔒(indication only)🟠 | **❌** | ❌ | C🟠(PO+OST) R🟠 U🟠 |
| **Pharmacies** | R🔒(rx context)🟠 | ❌ | ❌ | R🟠(PO+OST) X🟠 | **❌** | **❌** |
| **Medical Approval** | R✅🟠PUR | R✅🟠PUR | R✅🟠PUR | R✅🟠PUR | R✅🟠PUR | R✅🟠PUR |
| Medical Director | R✅🟠PUR | R✅🟠PUR | R✅🟠PUR | R✅🟠PUR | R✅🟠PUR | R✅🟠PUR |
| Case Managers | R🔒(summary)🟠ASG | R🔒(summary)🟠ASG | R🔒(coord)🟠ASG | R🔒🟠ASG | R🔒🟠ASG | R🔒🟠ASG |
| **Finance** | ❌ | ❌ | **❌** | ❌ | ❌ | ❌ |
| Provider Admin | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Network Team | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Org Admin | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Super Admin | R🧨 | R🧨 | R🧨 | R🧨 | R🧨 | R🧨 |

> **Hard-rule check (must always hold):** Reception row = all ❌. Doctors clinical = all 🟠TR. Labs `prescription` = ❌. Imaging `prescription` = ❌. Pharmacies `lab_result` = ❌ and `imaging_result` = ❌. Finance `diagnosis` = ❌ (and whole clinical row ❌). Medical Approval clinical = R✅ under `PUR`. Any change breaking these must be rejected at review.

### 3.3 Orders, approvals, provider, finance, admin, audit

| Role | order | approval_case | provider/contract/catalog | claim/invoice/payment | user/role_binding | audit_event |
|---|---|---|---|---|---|---|
| Beneficiary Mgmt | ❌ | ❌ | R🔒 | ❌ | ❌ | ❌ |
| Reception | R🔒(appt)🟠PO | ❌ | R🔒(own site)🟠PO | ❌ | ❌ | ❌ |
| Call Center | R🔒(status) C🟠(appt) U🟠(appt) | R🔒(status) | R🔒 | R🔒(balance) | ❌ | ❌ |
| Doctors | C🟠TR R🟠TR U🟠TR | C🟠TR R🟠TR | R🔒 | ❌ | ❌ | ❌ |
| Nurses | R🟠(care) | ❌ | R🔒 | ❌ | ❌ | ❌ |
| Labs | R🟠(PO+OST) X🟠 U🟠 | ❌ | R🔒(own)🟠PO | ❌ | ❌ | ❌ |
| Imaging | R🟠(PO+OST) X🟠 U🟠 | ❌ | R🔒(own)🟠PO | ❌ | ❌ | ❌ |
| Pharmacies | R🟠(PO+OST) X🟠 | ❌ | R🔒(own)🟠PO | C🟠(dispense claim) R🔒 | ❌ | ❌ |
| Medical Approval | R✅ | C✅ R✅ U✅ A✅🟠SOD | R🔒 | R🔒(status) | ❌ | ❌ |
| Medical Director | R✅ | R✅ U✅ A✅🟠SOD (override) | R🔒 | R🔒(cost) | ❌ | ❌ |
| Case Managers | R🟠ASG | C🟠ASG R🟠ASG U🟠ASG | R🔒 | R🔒🟠ASG | ❌ | ❌ |
| Finance | R🔒(billing code) | R🔒(status) | R🔒(rates) | C✅ R✅ U✅ A🟠SOD(release) E🔒 | ❌ | ❌ |
| Provider Admin | R🔒(own ops)🟠PO | ❌ | C🟠PO R🟠PO U🟠PO | R🔒(own)🟠PO | C🟠PO R🟠PO U🟠PO D🟠PO (own users) | ❌ |
| Network Team | ❌ | ❌ | C✅ R✅ U✅ A🟠SOD | R🔒(rates) | ❌ | ❌ |
| Org Admin | ❌ | ❌ | R🔒 | ❌ | C✅ R✅ U✅ D🟠SOD (tenant) | R🔒(access-review view) |
| Super Admin | R🧨 | R🧨 | R✅ | R🧨 | C✅ R✅ U✅ D✅ (global)🟠SOD | R🔒(read, cannot alter) |
| *audit service* | — | — | — | — | — | append-only (C only) |

**Export (E) note:** Export is a *distinct, elevated* action. Only Finance (financial reports, masked PII), Medical Director/Approval (case packets under `PUR`), Network Team (network reports), Org/Super Admin (operational, no PHI content) and reporting-designated users may Export, always 🔒-masked and always audited as a high-severity `data.export` event. **No provider-side role may bulk-export beneficiary data.**

---

## 4. Field-level rules for sensitive fields

Even when a role may Read an object, individual fields are governed independently. `visible` = rendered; `masked` = shown redacted/tokenized (e.g., `••••` or coarse category); `derived` = only a computed safety flag, never the raw value; `denied` = field stripped server-side before response.

| Role \ Field | `diagnosis` | `emr_note` | `prescription` | `lab_result` | `imaging_result` | `financials` | `pii` | `refugee_ref` |
|---|---|---|---|---|---|---|---|---|
| Beneficiary Mgmt | denied | denied | denied | denied | denied | denied | visible | visible |
| **Reception** | denied | denied | denied | denied | denied | denied | masked (min) | masked |
| Call Center | denied | denied | denied | denied | denied | masked (balance) | visible(verify) | masked |
| **Doctors** | visible🟠TR | visible🟠TR | visible🟠TR | visible🟠TR | visible🟠TR | denied | visible🟠TR | masked |
| Nurses | visible(problem)🟠 | visible🟠 | masked(admin) | visible🟠 | visible🟠 | denied | visible🟠 | denied |
| **Labs** | masked→indication🟠 | denied | **denied** | visible(own)🟠 | denied | denied | masked | denied |
| **Imaging** | masked→indication🟠 | denied | **denied** | denied | visible(own)🟠 | denied | masked | denied |
| **Pharmacies** | denied | denied | visible🟠 | **denied**→derived(safety flag) | **denied** | masked(copay) | masked | denied |
| **Medical Approval** | visible🟠PUR | visible🟠PUR | visible🟠PUR | visible🟠PUR | visible🟠PUR | masked | visible🟠PUR | masked |
| Medical Director | visible🟠PUR | visible🟠PUR | visible🟠PUR | visible🟠PUR | visible🟠PUR | visible(cost) | visible🟠PUR | masked |
| Case Managers | visible(coord)🟠ASG | masked(summary)🟠 | masked🟠 | masked🟠 | masked🟠 | masked🟠 | visible🟠ASG | masked |
| **Finance** | **denied** | denied | denied | denied | denied | visible | masked(min) | denied |
| Provider Admin | denied | denied | denied | denied | denied | masked(own) | denied | denied |
| Network Team | denied | denied | denied | denied | denied | visible(rates) | denied | denied |
| Org Admin | denied | denied | denied | denied | denied | denied | masked(dir) | denied |
| Super Admin | 🧨 | 🧨 | 🧨 | 🧨 | 🧨 | 🧨 | 🧨 | 🧨 |

**Key field-level encodings of the hard rules:**
- `Finance.diagnosis = denied` — claims are adjudicated on **billing/service codes + amounts**, never the diagnosis narrative. A procedure code that could reveal condition is exposed only at the minimum granularity needed to price/adjudicate.
- `Labs.prescription = denied`, `Imaging.prescription = denied` — the medication list is stripped from any order payload sent to labs/imaging.
- `Pharmacies.lab_result = denied → derived`, `Pharmacies.imaging_result = denied` — pharmacies receive a **derived safety flag** (e.g., "renal-adjust: yes", "interaction: none") computed server-side from results, never the raw result values.
- `Reception.*clinical = denied` — the entire clinical field set is stripped; Reception receives identity+appointment+eligibility-verdict only.
- `Doctors.* = visible🟠TR` — visible **only** while the treating relationship is active.

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
```

---

## 7. Deny-by-default & precedence

1. **Default deny.** Absent an explicit allow, access is denied at every layer.
2. **Field-deny overrides object-allow.** If a role may read an object but a field is `denied`, the field is stripped even on an allowed read.
3. **SoD-deny overrides role-allow.** An action is denied if the subject is conflicted for that specific record, regardless of role.
4. **Break-glass never silently widens.** `BG` allows only what its grant scopes, for its window, and always emits audit; it cannot be used to bypass SoD deny for self-approval.
5. **Environment can only restrict, never expand.** Failing device/MFA/IP checks can turn ✅ into ❌ but never the reverse.

Precedence order evaluated by the policy engine: `explicit-deny (field/SoD/env)` ▶ `break-glass-scoped-allow` ▶ `ABAC-conditional-allow` ▶ `RBAC-allow` ▶ `default-deny`.

---

## 8. Consistency rules with the Role Matrix

Every cell here must agree with [10-role-matrix.md §5–6](10-role-matrix.md). The six hard rules are re-verified here in §3.2 and §4. A CI check (design-lint) should assert:
- Reception has zero non-❌ cells in the clinical block.
- Finance `diagnosis` field = `denied` and clinical objects = ❌.
- Labs/Imaging `prescription` = `denied`.
- Pharmacies `lab_result`/`imaging_result` = `denied`.
- Doctors clinical reads all carry `TR`.
- Approval/Director clinical reads all carry `PUR`.

---

## 9. Cross-references
- Narrative role definitions & SoD table → **[10-role-matrix.md](10-role-matrix.md)**
- Enforcement points (gateway/service/RLS/field), Zero Trust, step-up, break-glass → **[18-security-model.md](18-security-model.md)**
- Audit of every read/write/approve/consume/export → **[19-audit-strategy.md](19-audit-strategy.md)**
- Regulatory mapping of minimization → **[20-compliance-checklist.md](20-compliance-checklist.md)**

> **Change control:** policy bundles are versioned, peer-reviewed by Security Architect + DPO, tested against a permission-regression suite (including the six hard rules), and deployed via the audited pipeline. No manual policy edits in production.
