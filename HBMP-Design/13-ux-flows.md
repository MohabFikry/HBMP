# 13 — UX Flows

> Back to [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [12-ui-wireframes.md](12-ui-wireframes.md) · [14-navigation-structure.md](14-navigation-structure.md) · [05-business-process-maps.md](05-business-process-maps.md) · [23-state-machines.md](23-state-machines.md)

Task-level UX flows for the primary journeys, expressed as Mermaid flowcharts. Each flow shows the **happy path plus error/edge branches and empty states**, and maps to the screens in [12-ui-wireframes.md](12-ui-wireframes.md) and the lifecycles in [23-state-machines.md](23-state-machines.md). Decision diamonds are labelled with the guard; terminal error states are marked `✕`, success `✓`.

Legend: rounded = screen/step · diamond = decision · `[/.../]` = system/async action · `✕` danger terminal · `✓` success terminal.

---

## 1. Register & activate a beneficiary (Phase 1)

```mermaid
flowchart TD
    A([Start: new beneficiary]) --> B[Reception/Registration opens New Beneficiary wizard]
    B --> C[Step 1: Identify — enter National ID / Passport / Refugee ID / UNHCR No]
    C --> D{Identifier already exists?}
    D -- Yes --> D1[Show existing record + merge/continue prompt]
    D1 --> E
    D -- No --> E[Step 2: Personal, contact, family, dependents]
    E --> F[Step 3: Upload documents]
    F --> G{Required docs present & valid type/size?}
    G -- No --> G1[Inline error: missing/oversized doc] --> F
    G -- Yes --> H[Step 4: Eligibility & coverage/policy assignment]
    H --> I[Submit for approval — status = Pending]
    I --> J[/Notify Beneficiary Mgmt approver/]
    J --> K{Approver decision}
    K -- Request info --> K1[Back to registration with notes] --> E
    K -- Reject --> KR([✕ Rejected — reason recorded, audited])
    K -- Approve --> L[Activate — status = Active, Member No issued]
    L --> M[/Emit BeneficiaryActivated event → eligibility snapshot built/]
    M --> N([✓ Active — card/QR future, ready for visits])
```

Empty state: wizard step with prefilled defaults when returning. All steps keyboard-navigable; errors summarized at top (`role="alert"`) and inline. See [US-001..US-004] in [32-user-stories.md](32-user-stories.md).

---

## 2. Reception eligibility check → visit (Phase 2 → 3)

```mermaid
flowchart TD
    A([Beneficiary arrives]) --> B[Reception search: ID / Passport / Card / Policy / Phone]
    B --> C{Match found?}
    C -- No --> C1[Empty: 'No match — try another identifier or register'] --> B
    C -- Yes --> D[Show minimum-necessary result card]
    D --> E{Eligibility status}
    E -- Active --> F[Show coverage summary + remaining limits + visit history summary]
    E -- Expired/Suspended/Blocked/Inactive --> G[Show status chip + guidance; block visit creation]
    G --> G1{Override path available?}
    G1 -- No --> GR([✕ Cannot proceed — refer to Case Manager])
    G1 -- Yes --> H
    E -- Pending Approval --> P[Show pending banner; limited actions]
    F --> H[Create/attach encounter → route to queue/appointment]
    H --> I([✓ Visit created — appears in Doctor/Nurse queue])
```

Reception **cannot see EMR** (min-necessary; [11-permission-matrix.md](11-permission-matrix.md)). Result card exposes only eligibility, coverage, remaining limits, and a visit-history *summary*.

---

## 3. Appointment: book / reschedule / no-show (Phase 3)

```mermaid
flowchart TD
    A([Appointment need]) --> B{Type}
    B -- Walk-in --> W[Add to queue for clinic/doctor]
    B -- Scheduled --> S[Pick clinic → doctor → slot from availability]
    B -- Referral --> R[Create from referral order]
    B -- Follow-up --> FU[Prefill from encounter]
    S --> C{Slot available?}
    C -- No --> C1[Offer waitlist or next slots] --> S
    C -- Yes --> D[Confirm appointment — status Scheduled]
    D --> E[/Reminder scheduled: SMS/WhatsApp future, in-app now/]
    E --> F{Day-of outcome}
    F -- Attends --> G([✓ Checked-in → encounter])
    F -- Reschedule --> H[Pick new slot → release old] --> D
    F -- Cancel --> I([Cancelled — slot released, audited])
    F -- No-show --> J[Mark No-show → waitlist backfill + policy flag]
    J --> K([Recorded — reporting + optional follow-up])
```

---

## 4. Consultation → orders & prescriptions (Phase 4)

```mermaid
flowchart TD
    A([Doctor opens encounter]) --> B{Treating relationship valid?}
    B -- No --> BR([✕ Access denied — not assigned])
    B -- Yes --> C[Review summary: history, diagnoses, allergies, vitals, meds]
    C --> D[Write SOAP note + diagnosis ICD-10]
    D --> E{Add services?}
    E -- Lab/Imaging --> F[Create investigation order — status Requested]
    E -- Medication --> G[Create e-prescription — status Draft→Submitted]
    E -- Referral --> H[Create referral — status Requested]
    F --> I{Requires pre-auth? high-cost/controlled}
    I -- Yes --> J[Route to Approvals — status PendingApproval]
    I -- No --> K[Order Active → available to providers]
    G --> L{Requires approval? expensive drug}
    L -- Yes --> J
    L -- No --> M[Prescription Submitted → available to pharmacies]
    J --> N[/Approvals decision → back to order/rx/]
    K --> O([✓ Encounter saved — orders/rx queued])
    M --> O
    H --> O
```

Drug-interaction/allergy alerts (future PBM rules) surface at prescription creation. See [23-state-machines.md](23-state-machines.md).

---

## 5. Lab/Imaging: consume order & upload result (Phase 5)

```mermaid
flowchart TD
    A([Provider opens queue]) --> B[Search: patient identifier / order number / QR future]
    B --> C{Order found & Active?}
    C -- No --> C1[Empty/'Not available or already used'] --> B
    C -- Yes --> D[View order lines authorized for this provider]
    D --> E[Select line and Consume]
    E --> F[/Atomic consume: lock line, check status=Active/]
    F --> G{Consume succeeded? unique guard}
    G -- No, already consumed --> GR([✕ Already used — cannot reuse])
    G -- Yes --> H[Perform investigation]
    H --> I[Upload result + attach report file scanned]
    I --> J{All lines fulfilled?}
    J -- Partial --> K[Order → PartiallyUsed; remaining lines stay Active]
    J -- All --> L[Order → Completed]
    K --> M([✓ Result available to ordering doctor/approvals])
    L --> M
```

Labs/imaging **cannot see prescriptions**. The consume step is idempotent and duplicate-proof ([0A §7](0A-DESIGN-FOUNDATIONS.md), [24-sequence-diagrams.md](24-sequence-diagrams.md)).

---

## 6. Pharmacy: partial dispense & substitution (Phase 6)

```mermaid
flowchart TD
    A([Pharmacy opens queue]) --> B[Search: Rx number / Patient ID / Policy / Passport / Member No]
    B --> C{Rx found & dispensable?}
    C -- No/Expired/Completed --> CR([✕ Reject: expired or already completed])
    C -- Yes --> D[View prescription lines + remaining quantities]
    D --> E{Stock available?}
    E -- No --> E1[Out-of-stock workflow: partial/none + flag] 
    E -- Substitution needed --> E2[Choose approved alternative] --> F
    E -- Yes --> F[Enter dispensed qty ≤ remaining; batch + expiry]
    E1 --> F
    F --> G[/Record dispense_event, decrement remaining, audit/]
    G --> H{All lines fully dispensed?}
    H -- Partial --> I[Rx → PartiallyDispensed; remainder stays available]
    H -- All --> J[Rx → Dispensed]
    I --> K([✓ Dispense recorded])
    J --> K
```

Pharmacy **cannot see investigation results**. Batch/expiry captured; expired-drug or completed-Rx dispensing rejected.

---

## 7. Medical approval: request → decision (Phase 7)

```mermaid
flowchart TD
    A([Auth request created]) --> B[Approvals worklist — status Submitted→UnderReview]
    B --> C[Reviewer opens: EMR, clinical notes, supporting docs]
    C --> D{Decision}
    D -- Approve --> E[Approved — record rationale, reviewer, timestamp]
    D -- Partial --> F[PartiallyApproved — approved lines only]
    D -- Reject --> G[Rejected — mandatory reason]
    D -- Request info --> H[InfoRequested → back to requester]
    D -- Emergency/Override/Manual --> I[EmergencyApproved/Overridden — extra justification + break-glass audit]
    E --> J[/Notify requester + provider; update order/rx/]
    F --> J
    G --> J
    I --> J
    H --> K([Awaiting info])
    J --> L([✓ Decision audited immutably])
```

Manual authorization allows the approval team to search a member directly and create an authorization without a provider submission. Every decision records reviewer, timestamp, decision, rationale, and rejection reason ([19-audit-strategy.md](19-audit-strategy.md)).

---

## 8. Referral (cross-phase)

```mermaid
flowchart TD
    A([Doctor creates referral]) --> B[Referral Requested]
    B --> C{Receiving provider accepts?}
    C -- No --> C1[Reassign or Cancelled] 
    C -- Yes --> D[Accepted → Scheduled appointment]
    D --> E[Completed after encounter]
    E --> F([✓ Loop closed — result back to referring doctor])
```

---

### Cross-references
- Screens: [12-ui-wireframes.md](12-ui-wireframes.md) · Navigation: [14-navigation-structure.md](14-navigation-structure.md)
- Process detail: [05-business-process-maps.md](05-business-process-maps.md) · [06-bpmn-diagrams.md](06-bpmn-diagrams.md)
- Lifecycles: [23-state-machines.md](23-state-machines.md) · Interactions: [24-sequence-diagrams.md](24-sequence-diagrams.md)
- Stories: [32-user-stories.md](32-user-stories.md)
