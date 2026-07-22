# 06 — BPMN-Style Diagrams (Swimlane Models)

> Part of the **Mersal Healthcare Benefit Management Platform (HBMP)** design workspace.
> Back to: [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [05-business-process-maps.md](05-business-process-maps.md) · [23-state-machines.md](23-state-machines.md) · [24-sequence-diagrams.md](24-sequence-diagrams.md)

This document renders BPMN-style process models **emulated in Mermaid `flowchart`**, using `subgraph` blocks as **pools/lanes** (one per actor/system) and explicit **gateway** labels. Because Mermaid has no native BPMN shapes, we adopt the notation convention below and apply it consistently.

---

## BPMN Notation Conventions (Mermaid emulation)

| BPMN element | Meaning | Mermaid emulation |
|---|---|---|
| Start event | Process trigger | `([Start ...])` stadium node, prefixed `Start` |
| End event | Process outcome | `([End ...])` stadium node, prefixed `End` |
| Task / Activity | Unit of work | `[Rectangle task]` |
| Service task | Automated/system task | `[[Double-border task]]` |
| Exclusive gateway (XOR) | One path chosen | `{XOR: condition?}` diamond |
| Parallel gateway (AND) | All paths taken | `{AND: fork/join}` diamond, fan-out/fan-in |
| Pool / Lane | Actor or system boundary | `subgraph LaneName` |
| Message / event flow | Async signal | dashed arrow `-.->` |
| Data / audit store | Persistent write | `[(Cylinder)]` |

**Gateway labeling rule:** every gateway is prefixed `XOR:` or `AND:` so the split semantics are unambiguous. XOR = exactly one outgoing path; AND = all outgoing paths concurrently (join waits for all).

---

## BPMN-1 — Registration & Activation

**Pools:** Beneficiary · Registration/Beneficiary Mgmt · patient-service · policy-service · notification-service · audit-service.

```mermaid
flowchart TD
    subgraph Ben[Lane: Beneficiary]
        S1([Start: Presents for enrollment])
        B1[Provide identity + demographics]
        B2[Provide missing documents]
    end
    subgraph Reg[Lane: Registration Team]
        R1[Capture intake]
        R2{XOR: Documents valid?}
        R3[Request missing documents]
        R4[Assign benefit package]
        R5[Activate member]
        R6[Issue digital card + member ID]
    end
    subgraph PS[Lane: patient-service]
        P1[[Dedup + create record: Pending]]
        P2[[Set state Active]]
    end
    subgraph POL[Lane: policy-service]
        POL1[[Bind policy / coverage]]
    end
    subgraph NOT[Lane: notification-service]
        N1[[Send reminder / welcome]]
    end
    subgraph AUD[Lane: audit-service]
        A1[(Enrollment + activation events)]
    end
    E1([End: Member Active])

    S1 --> B1 --> R1 --> P1 --> R2
    R2 -->|No| R3 -.-> N1 -.-> B2 --> R1
    R2 -->|Yes| POL1 --> R4 --> P2 --> R5 --> R6
    R6 -.-> N1
    R6 --> A1 --> E1
```

**Gateways:** R2 is an **XOR** on document validity. No parallel gateways in this model.

---

## BPMN-2 — Eligibility Check at Reception

**Pools:** Beneficiary · Reception (Registration) · eligibility · policy-service · patient-service · approvals-service · audit-service.

```mermaid
flowchart TD
    subgraph Ben[Lane: Beneficiary]
        S1([Start: Arrives / requests service])
    end
    subgraph Rec[Lane: Reception]
        R1[Enter member ID + service type]
        R2{XOR: Decision routing}
        R3[Inform beneficiary: eligible]
        R4[Inform beneficiary: not eligible]
    end
    subgraph ELG[Lane: eligibility]
        E1[[Resolve member]]
        E2{XOR: Member Active?}
        E3{XOR: Covered by policy?}
        E4{XOR: Within limits + waiting period?}
        E5[[Compose ELIGIBLE result]]
        E6[[Compose INELIGIBLE result]]
    end
    subgraph PS[Lane: patient-service]
        P1[[Member state lookup]]
    end
    subgraph POL[Lane: policy-service]
        POL1[[Coverage + limits lookup]]
    end
    subgraph APR[Lane: approvals-service]
        AP1[[Route over-limit to review]]
        AP2{XOR: Approved?}
    end
    subgraph AUD[Lane: audit-service]
        A1[(Eligibility decision record)]
    end
    Eend([End: Decision returned])

    S1 --> R1 --> E1 --> P1 --> E2
    E2 -->|No| E6
    E2 -->|Yes| POL1 --> E3
    E3 -->|No| E6
    E3 -->|Yes| E4
    E4 -->|Exceeded| AP1 --> AP2
    AP2 -->|Yes| E5
    AP2 -->|No| E6
    E4 -->|OK| E5
    E5 --> R2 -->|Eligible| R3
    E6 --> R2 -->|Ineligible| R4
    R3 --> A1
    R4 --> A1
    A1 --> Eend
```

**Gateways:** E2/E3/E4/AP2/R2 are all **XOR**. Finance/clinical detail is excluded (data minimization: reception sees coverage flags only).

---

## BPMN-3 — Appointment Scheduling (incl. Waitlist / Queue)

**Pools:** Beneficiary · Call Center / Appointment Team · provider-service (scheduling) · notification-service · audit-service.

```mermaid
flowchart TD
    subgraph Ben[Lane: Beneficiary]
        S1([Start: Appointment need])
        B1[Accept offered slot]
    end
    subgraph App[Lane: Appointment Team / Call Center]
        A1[Confirm eligibility ref P2]
        A2[Search slots by specialty + priority]
        A3{XOR: Slot available?}
        A4[Book slot]
        A5[Add to waitlist / queue]
        A6[Promote from waitlist by priority]
    end
    subgraph PRV[Lane: provider-service scheduling]
        PR1[[Reserve slot -> Scheduled]]
        PR2[[Waitlist entry created]]
        PR3{XOR: Slot freed cancel/no-show?}
    end
    subgraph NOT[Lane: notification-service]
        N1[[Confirmation + reminders]]
        N2[[Waitlist promotion offer]]
    end
    subgraph AUD[Lane: audit-service]
        AU1[(Booking / waitlist events)]
    end
    E1([End: Appointment confirmed])
    E2([End: Waitlist expired])

    S1 --> A1 --> A2 --> A3
    A3 -->|Yes| A4 --> PR1 --> B1 --> N1 --> AU1 --> E1
    A3 -->|No| A5 --> PR2 --> PR3
    PR3 -->|Yes| A6 -.-> N2 -.-> A4
    PR3 -->|Expires| E2
```

**Gateways:** A3 and PR3 are **XOR**. Waitlist promotion (A6) re-enters the booking task via message flow.

---

## BPMN-4 — Investigation Order → Lab / Imaging Fulfillment

**Pools:** Beneficiary · Doctor · orders-service · Lab / Imaging Center · emr-service · notification-service · audit-service.
Highlights the **atomic, idempotent consume** with an XOR on remaining lines (PartiallyUsed vs Completed).

```mermaid
flowchart TD
    subgraph Doc[Lane: Doctor]
        D1([Start: Investigation needed])
        D2[Create investigation order]
    end
    subgraph ORD[Lane: orders-service]
        O1[[Order Requested]]
        O2{XOR: Needs approval?}
        O3[[Order Active]]
        O4[[Atomic consume selected lines - idempotent]]
        O5{XOR: All lines used?}
        O6[[Order PartiallyUsed]]
        O7[[Order Completed]]
    end
    subgraph APR[Lane: approvals-service]
        AP1[[PendingApproval review]]
        AP2{XOR: Approved?}
    end
    subgraph Ben[Lane: Beneficiary]
        B1[Present order at lab/imaging]
    end
    subgraph Lab[Lane: Lab / Imaging Center]
        L1{XOR: Order state Active/PartiallyUsed?}
        L2[Reject + reason]
        L3[Select line items]
        L4[Perform tests / imaging]
        L5[Upload results]
    end
    subgraph EMR[Lane: emr-service]
        EM1[[Attach results to record]]
    end
    subgraph NOT[Lane: notification-service]
        N1[[Results ready]]
    end
    subgraph AUD[Lane: audit-service]
        AU1[(Consume + result events)]
    end
    E1([End: Fulfillment recorded])

    D1 --> D2 --> O1 --> O2
    O2 -->|Yes| AP1 --> AP2
    AP2 -->|Approved| O3
    AP2 -->|Rejected| AU1
    O2 -->|No| O3
    O3 --> B1 --> L1
    L1 -->|No| L2 --> AU1
    L1 -->|Yes| L3 --> O4 --> O5
    O5 -->|No| O6 --> L4
    O5 -->|Yes| O7 --> L4
    L4 --> L5 --> EM1 -.-> N1
    EM1 --> AU1 --> E1
```

**Gateways:** O2/O5/AP2/L1 are **XOR**. The **consume task O4** is the atomicity boundary: unused lines survive, used lines are locked, replays are idempotent by `(orderId, lineId, consumeToken)`.

---

## BPMN-5 — e-Prescription → Pharmacy Dispensing

**Pools:** Doctor · Beneficiary · pharmacy · approvals-service · policy-service · notification-service · audit-service.
Covers **partial dispensing**, **substitution with approved alternatives**, and **out-of-stock** workflow, and rejects expired/completed prescriptions.

```mermaid
flowchart TD
    subgraph Doc[Lane: Doctor]
        D1([Start: Medication needed])
        D2[Create e-prescription Draft -> Submitted]
    end
    subgraph APR[Lane: approvals-service]
        AP0{XOR: Needs approval?}
        AP1[[Review]]
        AP2{XOR: Approved?}
    end
    subgraph Ben[Lane: Beneficiary]
        B1[Present prescription at pharmacy]
    end
    subgraph Ph[Lane: Pharmacy]
        P1{XOR: State Approved/PartiallyDispensed?}
        P2[Reject: expired/completed + reason]
        P3{XOR: All items in stock?}
        P4[Out-of-stock workflow]
        P5{XOR: Approved substitute available?}
        P6[Apply approved substitution]
        P7[Flag backorder / partial]
        P8[[Atomic dispense of available lines]]
        P9{XOR: All lines dispensed?}
        P10[[PartiallyDispensed - remaining available]]
        P11[[Dispensed]]
    end
    subgraph POL[Lane: policy-service]
        POL1[[Approved-alternatives list]]
    end
    subgraph NOT[Lane: notification-service]
        N1[[Dispense / reject notice]]
    end
    subgraph AUD[Lane: audit-service]
        AU1[(Dispense + substitution events)]
    end
    E1([End: Dispensing recorded])

    D1 --> D2 --> AP0
    AP0 -->|Yes| AP1 --> AP2
    AP2 -->|Approved| B1
    AP2 -->|Rejected| AU1
    AP0 -->|No| B1
    B1 --> P1
    P1 -->|No| P2 -.-> N1
    P1 -->|Yes| P3
    P3 -->|Yes| P8
    P3 -->|Some out| P4 --> P5
    P5 -->|Yes lookup| POL1 --> P6 --> P8
    P5 -->|No| P7 --> P8
    P8 --> P9
    P9 -->|No| P10 -.-> N1
    P9 -->|Yes| P11 -.-> N1
    P10 --> AU1
    P11 --> AU1
    P2 --> AU1
    AU1 --> E1
```

**Gateways:** AP0/AP2/P1/P3/P5/P9 are **XOR**. Substitution is constrained to the policy-approved alternatives list; anything outside routes back to approvals. Dispense (P8) is atomic/idempotent on prescription lines.

---

## BPMN-6 — Medical Approval / Authorization (Partial, Override, Emergency, Manual)

**Pools:** Requester (Doctor/Case Manager) · approvals-service · Medical Approval Team · Medical Director · policy-service · notification-service · audit-service.
Includes **PartiallyApproved**, **Overridden**, **EmergencyApproved**, **InfoRequested**, and manual authorization.

```mermaid
flowchart TD
    subgraph Req[Lane: Requester]
        S1([Start: Authorization needed])
        R1[Compile clinical justification]
        R2[Provide requested info]
    end
    subgraph APR[Lane: approvals-service]
        A1[[Draft -> Submitted]]
        A2{XOR: Emergency?}
        A3[[UnderReview]]
        A4[[Emit decision + notify]]
    end
    subgraph MAT[Lane: Medical Approval Team]
        M1[Assess vs policy + clinical]
        M2{XOR: Outcome?}
    end
    subgraph POL[Lane: policy-service]
        POL1[[Coverage limits + rules]]
    end
    subgraph Dir[Lane: Medical Director]
        DI1{XOR: Override out-of-policy?}
        DI2[Record override + justification]
        DI3[Fast-track EmergencyApproved]
    end
    subgraph NOT[Lane: notification-service]
        N1[[Decision to requester]]
    end
    subgraph AUD[Lane: audit-service]
        AU1[(Decision + justification + actor)]
    end
    E1([End: Authorization finalized])

    S1 --> R1 --> A1 --> A2
    A2 -->|Yes| DI3 --> A4
    A2 -->|No| A3 --> M1 --> POL1 --> M2
    M2 -->|Within policy| A4
    M2 -->|Partly| A4
    M2 -->|Missing info| R2 --> A3
    M2 -->|Out of policy| DI1
    DI1 -->|Yes| DI2 --> A4
    DI1 -->|No| A4
    A4 -.-> N1
    A4 --> AU1 --> E1
```

**Gateways:** A2/M2/DI1 are **XOR**. M2 fans to four outcomes (Approved, PartiallyApproved, InfoRequested, out-of-policy→override branch). Emergency path bypasses standard review with mandatory retrospective audit. Manual authorization = Medical Director acting directly at DI2 with justification.

---

## Notes on Parallelism (AND gateways)

Most HBMP flows are decision-heavy (XOR). AND gateways appear where independent work runs concurrently — e.g. at **encounter close** an order, prescription, and referral may be emitted in parallel:

```mermaid
flowchart TD
    C0([Start: Close encounter]) --> AND1{AND: fork emissions}
    AND1 --> T1[[Emit investigation order event]]
    AND1 --> T2[[Emit prescription event]]
    AND1 --> T3[[Emit referral event]]
    T1 --> AND2{AND: join}
    T2 --> AND2
    T3 --> AND2
    AND2 --> AU[(audit-service)]
    AU --> E([End: Encounter completed])
```

The **AND join** waits for all three emissions before the encounter is marked complete, guaranteeing no orphaned clinical artifacts.

---

## Cross-References
- Narrative process maps + pain points: [05-business-process-maps.md](05-business-process-maps.md)
- State machines behind each object (Order, Prescription, Authorization, ...): [23-state-machines.md](23-state-machines.md)
- Sequence diagrams across microservices: [24-sequence-diagrams.md](24-sequence-diagrams.md)
- Foundations & glossary: [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
