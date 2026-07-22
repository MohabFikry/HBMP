# 24 — Sequence Diagrams (Microservice Interactions)

> Part of the **Mersal Healthcare Benefit Management Platform (HBMP)** design workspace.
> Back to: [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [05-business-process-maps.md](05-business-process-maps.md) · [06-bpmn-diagrams.md](06-bpmn-diagrams.md) · [23-state-machines.md](23-state-machines.md)

`sequenceDiagram` models for the critical cross-service interactions. Conventions:
- **Solid arrow `->>`** = synchronous request; **dashed `-->>`** = response.
- **`-)`** = asynchronous publish onto the **event bus** (fire-and-forget).
- Every state-changing interaction includes an **audit-service** write.
- Data-minimization boundaries are noted where a service is deliberately *not* called or receives a reduced projection.

**Services:** `api-gateway`, `identity`, `patient-service`, `policy-service`, `eligibility`, `emr-service`, `orders-service`, `approvals-service`, `provider-service`, `pharmacy`, `notification-service`, `audit-service`, plus the async **event bus**.

---

## SEQ-1 — Login + MFA

```mermaid
sequenceDiagram
    autonumber
    actor U as User (staff/beneficiary)
    participant GW as api-gateway
    participant ID as identity
    participant NOT as notification-service
    participant BUS as event-bus
    participant AUD as audit-service

    U->>GW: POST /login (username, password)
    GW->>ID: authenticate(credentials)
    ID-->>GW: primary OK, MFA required (challengeId)
    GW-->>U: MFA challenge
    ID-)NOT: send OTP (async)
    NOT-->>U: OTP via SMS/email
    U->>GW: POST /login/mfa (challengeId, otp)
    GW->>ID: verifyMfa(challengeId, otp)
    ID-->>GW: access + refresh tokens (scoped roles)
    ID-)BUS: publish AuthSucceeded
    BUS-)AUD: persist login event
    GW-->>U: 200 session established
    Note over ID,AUD: Failed attempts publish AuthFailed -> audit + lockout policy
```

---

## SEQ-2 — Eligibility Check

```mermaid
sequenceDiagram
    autonumber
    actor R as Reception
    participant GW as api-gateway
    participant EL as eligibility
    participant PS as patient-service
    participant POL as policy-service
    participant APR as approvals-service
    participant BUS as event-bus
    participant AUD as audit-service

    R->>GW: GET /eligibility?memberId&serviceType
    GW->>EL: check(memberId, serviceType)
    EL->>PS: getMemberState(memberId)
    PS-->>EL: state=Active (minimized: no clinical data)
    EL->>POL: coverage(serviceType) + limits
    POL-->>EL: covered=true, remainingLimit
    alt Within limits
        EL-->>GW: ELIGIBLE (coverage terms)
    else Over limit
        EL->>APR: openReview(memberId, serviceType)
        APR-->>EL: pending / decision ref
        EL-->>GW: NEEDS_REVIEW (routed to approvals)
    end
    EL-)BUS: publish EligibilityEvaluated
    BUS-)AUD: persist decision (no diagnosis fields)
    GW-->>R: result
    Note over EL,POL: Reception receives coverage flags only (data minimization)
```

---

## SEQ-3 — Create Consultation + Investigation Order + Prescription

```mermaid
sequenceDiagram
    autonumber
    actor D as Doctor
    participant GW as api-gateway
    participant EMR as emr-service
    participant ORD as orders-service
    participant RX as pharmacy
    participant APR as approvals-service
    participant POL as policy-service
    participant BUS as event-bus
    participant AUD as audit-service

    D->>GW: POST /encounters (diagnosis, notes)
    GW->>EMR: createEncounter(...)
    EMR-)BUS: EncounterStarted
    EMR-->>GW: encounterId

    D->>GW: POST /orders (investigation lines)
    GW->>ORD: createOrder(encounterId, lines)
    ORD->>POL: isGated(services)?
    POL-->>ORD: gated=true
    ORD->>APR: submit(orderId) [PendingApproval]
    APR-->>ORD: accepted
    ORD-)BUS: OrderPendingApproval
    ORD-->>GW: orderId (PendingApproval)

    D->>GW: POST /prescriptions (rx lines)
    GW->>RX: createPrescription(encounterId, lines) [Draft->Submitted]
    RX->>POL: isGated(meds)?
    POL-->>RX: gated=false
    RX-->>GW: prescriptionId (Approved)
    RX-)BUS: RxApproved

    BUS-)AUD: persist encounter/order/rx events
    Note over EMR,RX: emr links order & rx to encounter; labs/pharmacy see only their own artifact (minimization)
```

---

## SEQ-4 — Lab Consuming an Order (Atomic, Idempotent)

```mermaid
sequenceDiagram
    autonumber
    actor L as Lab Tech
    participant GW as api-gateway
    participant ORD as orders-service
    participant EMR as emr-service
    participant NOT as notification-service
    participant BUS as event-bus
    participant AUD as audit-service

    L->>GW: POST /orders/{id}/consume (lineIds, consumeToken)
    GW->>ORD: consume(orderId, lineIds, consumeToken)
    alt Order not Active/PartiallyUsed
        ORD-->>GW: 409 Rejected (expired/completed/cancelled)
        ORD-)BUS: OrderConsumeRejected
    else Valid + token unseen
        Note over ORD: BEGIN atomic tx
        ORD->>ORD: verify lines available AND token unused
        ORD->>ORD: mark selected lines used
        ORD->>ORD: recompute -> PartiallyUsed | Completed
        Note over ORD: COMMIT (unused lines stay available)
        ORD-->>GW: 200 consumed (new state)
        ORD-)BUS: OrderLinesConsumed
    else Token replay (idempotent)
        ORD-->>GW: 200 prior result (no state change)
    end
    L->>GW: POST /orders/{id}/results
    GW->>EMR: attachResults(orderId, results)
    EMR-)BUS: ResultsReady
    BUS-)NOT: notify beneficiary + doctor
    BUS-)AUD: persist consume + result (no-reuse enforced)
    Note over ORD,AUD: consumeToken makes duplicate usage impossible; used lines never reusable
```

---

## SEQ-5 — Pharmacy Partial Dispense (with Substitution / Out-of-Stock)

```mermaid
sequenceDiagram
    autonumber
    actor P as Pharmacist
    participant GW as api-gateway
    participant RX as pharmacy
    participant POL as policy-service
    participant APR as approvals-service
    participant NOT as notification-service
    participant BUS as event-bus
    participant AUD as audit-service

    P->>GW: POST /prescriptions/{id}/dispense (lines, dispenseToken)
    GW->>RX: dispense(rxId, lines, dispenseToken)
    alt Rx expired/completed/rejected
        RX-->>GW: 409 Rejected + reason
        RX-)BUS: RxDispenseRejected
    else Valid
        RX->>RX: check stock per line
        opt Out of stock line
            RX->>POL: approvedAlternatives(drug)?
            POL-->>RX: substitute list
            alt Approved substitute exists
                RX->>RX: apply substitution
            else No substitute
                RX->>APR: requestOverride(line) (optional)
                APR-->>RX: decision
                RX->>RX: flag backorder / skip line
            end
        end
        Note over RX: BEGIN atomic tx (dispenseToken)
        RX->>RX: mark dispensed lines used
        RX->>RX: recompute -> PartiallyDispensed | Dispensed
        Note over RX: COMMIT (remaining lines available)
        RX-->>GW: 200 dispensed (state, substitutions)
        RX-)BUS: RxLinesDispensed
    end
    BUS-)NOT: notify beneficiary (partial + pickup remainder)
    BUS-)AUD: persist dispense + substitution + OOS
    Note over RX,AUD: pharmacy never queries investigation results (data minimization)
```

---

## SEQ-6 — Approval Request → Decision → Notification

```mermaid
sequenceDiagram
    autonumber
    actor M as Medical Approval Team
    participant GW as api-gateway
    participant APR as approvals-service
    participant POL as policy-service
    participant EMR as emr-service
    participant ORD as orders-service
    participant NOT as notification-service
    participant BUS as event-bus
    participant AUD as audit-service

    Note over APR: Triggered by OrderPendingApproval (SEQ-3) via bus
    BUS-)APR: OrderPendingApproval (authId)
    M->>GW: GET /approvals/{authId}
    GW->>APR: load(authId) [UnderReview]
    APR->>EMR: getClinicalJustification(encounterId)
    EMR-->>APR: justification (scoped)
    APR->>POL: rules + limits(serviceType)
    POL-->>APR: policy terms
    M->>GW: POST /approvals/{authId}/decide (outcome, justification)
    alt Approved / PartiallyApproved
        APR->>ORD: setApproved(orderId, scope)
        ORD-)BUS: OrderApproved
    else Rejected
        APR-)BUS: AuthRejected
        Note over APR: Director may POST override -> Overridden (mandatory justification)
    else InfoRequested
        APR-)BUS: AuthInfoRequested
    end
    APR-)BUS: AuthDecided
    BUS-)NOT: notify requester + beneficiary
    BUS-)AUD: persist decision + justification + actor
    GW-->>M: 200 decision recorded
```

### Emergency fast-track variant
```mermaid
sequenceDiagram
    autonumber
    actor DIR as Medical Director
    participant GW as api-gateway
    participant APR as approvals-service
    participant BUS as event-bus
    participant AUD as audit-service

    DIR->>GW: POST /approvals/{authId}/emergency (justification)
    GW->>APR: fastTrack(authId)
    APR->>APR: state -> EmergencyApproved
    APR-)BUS: AuthEmergencyApproved
    BUS-)AUD: persist emergency decision (retrospective review flag)
    APR-->>GW: 200 (service may proceed immediately)
    Note over APR,AUD: Retrospective review scheduled; justification mandatory
```

---

## SEQ-7 — Referral (Request → Accept → Schedule → Complete)

```mermaid
sequenceDiagram
    autonumber
    actor D as Doctor
    participant GW as api-gateway
    participant EMR as emr-service
    participant PRV as provider-service
    participant NOT as notification-service
    participant BUS as event-bus
    participant AUD as audit-service

    D->>GW: POST /referrals (specialty, reason)
    GW->>EMR: createReferral(encounterId) [Requested]
    EMR-)BUS: ReferralRequested
    BUS-)PRV: match receiving provider
    PRV->>PRV: find in-network provider (specialty)
    PRV-)BUS: ReferralAccepted
    Note over PRV: appointment booked via provider-service scheduling
    PRV->>PRV: reserve slot [Scheduled]
    PRV-)BUS: ReferralScheduled
    BUS-)NOT: notify beneficiary (appointment details)
    Note over EMR,PRV: On visit, doctor closes encounter -> ReferralCompleted
    EMR-)BUS: ReferralCompleted
    BUS-)AUD: persist referral lifecycle events
```

---

## Event Bus Summary

| Event | Publisher | Key Subscribers | Purpose |
|---|---|---|---|
| `AuthSucceeded` / `AuthFailed` | identity | audit-service | Login audit + lockout |
| `EligibilityEvaluated` | eligibility | audit-service | Decision trail |
| `EncounterStarted` / `EncounterCompleted` | emr-service | orders, pharmacy, audit | Clinical lifecycle |
| `OrderPendingApproval` / `OrderApproved` / `OrderRejected` | orders / approvals | approvals, notification, audit | Order gating |
| `OrderLinesConsumed` / `OrderCompleted` | orders-service | emr, notification, audit | Atomic consume trail |
| `RxApproved` / `RxLinesDispensed` / `RxDispensed` | pharmacy | notification, audit | Dispensing trail |
| `AuthDecided` / `AuthEmergencyApproved` / `AuthOverridden` | approvals-service | notification, audit | Authorization outcomes |
| `Referral*` | emr / provider | provider, notification, audit | Referral lifecycle |

**Reliability notes:**
- All async publishes use at-least-once delivery; consumers are **idempotent** (dedupe by eventId).
- `audit-service` subscribes to every domain event — audit is never in the synchronous critical path but is guaranteed by durable bus delivery.
- Consume/dispense tokens carry across bus retries so replays never double-apply.

---

## Cross-References
- Narrative process maps: [05-business-process-maps.md](05-business-process-maps.md)
- BPMN swimlanes: [06-bpmn-diagrams.md](06-bpmn-diagrams.md)
- State machines + transition tables: [23-state-machines.md](23-state-machines.md)
- Foundations & glossary: [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
