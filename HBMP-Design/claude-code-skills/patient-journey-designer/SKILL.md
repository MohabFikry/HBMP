---
name: Patient Journey Designer
description: Encodes Mersal's end-to-end 7-phase beneficiary journey, its touchpoints, minimum-necessary data per stage, hand-offs, and launch-vs-future channels. Use when designing user flows, screens, wireframes, process maps, or navigation, or when reviewing a feature for where it sits in the journey and what data it may show.
---

# Patient Journey Designer

## Purpose
Ensure any flow, screen, or process map is anchored in Mersal's canonical **7-phase beneficiary journey**, shows only minimum-necessary data at each stage, respects the emotional low points, and hands off correctly between actors and services. The journey is the source for process models, state machines, and UX flows — designs must stay consistent with it.

## When to use / when not to use
- **Use when:** designing or reviewing user flows, wireframes, screens, navigation, or business-process/BPMN maps; deciding which fields a role sees at a step; sequencing hand-offs between reception, clinician, lab/imaging, pharmacy, and approvals; identifying where SMS/WhatsApp/QR are future.
- **Not for:** backend service boundaries (Platform Architect), schema/field mechanics (Database Architect), or the formal status-transition guards (those live in the state machines — reference them, don't restate the algebra).

## Mersal domain knowledge & rules
**The 7 phases (journey is NOT strictly linear):**
1. **Registration** — beneficiary arrives with any documents; identity captured, de-duplicated, member issued. Services: Beneficiaries, Documents. State: Member `Pending → Active`. Exit: member identity issued. *Emotional low point: registration anxiety — no dead-ends, dignity in framing.*
2. **Eligibility** — real-time, benefit-aware check at ANY point of service; **re-checked at every POS**, not once. Services: Eligibility, Coverage. Exit: eligible/ineligible answered.
3. **Appointments** — eligibility-gated booking prevents wasted trips; reminder in-app/email. Services: Appointments, Provider Network, Eligibility. Exit: visit booked.
4. **Consultation** — triage/vitals (nurse), longitudinal review (history, allergies, active meds, prior results), structured SOAP note, orders/prescriptions created. Services: EMR, Orders, Prescriptions. State: `ENC-*`; Order `Requested`; Rx `Draft → Submitted`.
5. **Lab & Imaging** — order appears in provider-**isolated** queue; **consumed once, atomically**; result uploaded and made visible via events. Services: Orders, Provider Network, Documents, Authorizations. State: Order `Active → PartiallyUsed → Completed`.
6. **Pharmacy** — prescription dispensed full or **partial** (partial is first-class; balance owed persists); consume-once integrity across both events. Services: Prescriptions, Coverage. State: Rx `PartiallyDispensed → Dispensed`.
7. **Approval (as needed)** — invoked *within* Phases 5/6 when a high-cost/controlled item is ordered; 100% routed (no out-of-band approvals), evidence + rationale + audit, TAT measured (target ≤24h median). Services: Authorizations, EMR (evidence). State: Authorization `Submitted → UnderReview → decision`.
- **Minimum-necessary data per stage** is a hard rule: every "data shown" is a min-necessary claim enforced by permission matrix + security model. Provider queues (lab/imaging/pharmacy) show only the order/Rx + **minimum beneficiary identity**; approvers see evidence + coverage/limits + minimum identity. Identifier values are SPI — never surface casually.
- **Hand-offs are event-driven:** result upload → `OrderConsumed`/result events close the loop to the care team; approval decision → notification to clinician/queue; no phone chasing.
- **Key guarantees to make visible in UX:** real-time eligibility, provider isolation, consume-once atomicity (duplicate use impossible), first-class partial dispensing, immutable auditable decisions, approval status shown before a provider performs a gated service.
- **Channel maturity (launch vs future):** launch = in-app + email notifications, member number handoff, staff-served (no beneficiary self-service), upload-to-Documents result exchange. **Future = SMS/WhatsApp reminders, QR beneficiary/order handoff, beneficiary mobile app, FHIR/HL7 partner exchange.** Tag any such step **[FUTURE]**.

## Key entities, states & invariants
- Business keys surfaced to users: `MRS-M-*`, `ENC-*`, `ORD-*`, `RX-*`, `AUTH-*`, `REF-*`.
- Canonical states per phase come from `../../23-state-machines.md`; UX must reflect legal transitions only (e.g., pharmacy must reject a presented Expired/Cancelled/fully-Dispensed Rx).
- Non-linearity invariants: eligibility recurs at every POS; approval is a sub-flow inside fulfillment, not a separate linear step.

## How to apply
- Start every flow by locating its phase(s) and actor persona, then list the **minimum-necessary** fields for that step — justify anything beyond it.
- Model hand-offs as events/queues, not synchronous coupling; show downstream visibility as a consequence of an event.
- Design the emotional low points (registration anxiety, approval uncertainty, partial-dispense constraint) as priority UX targets: reassurance, transparent status, "what's owed" clarity.
- Gate bookings and fulfillment on eligibility/approval status in the UI; show approval status before a provider can perform a gated service.
- Mark SMS/WhatsApp/QR/self-service/FHIR steps as **[FUTURE]**; at launch use in-app + email + member number.
- In reviews, flag: data over-exposure at a stage, missing eligibility re-check at a POS, provider isolation leaks, treating partial dispensing as a workaround, or linear approval placement.

## Canonical references
- Journey maps, scenarios, min-necessary data, channel maturity: `../../04-patient-journey-maps.md`
- Business process maps: `../../05-business-process-maps.md`; UX flows: `../../13-ux-flows.md`
- State machines behind each phase: `../../23-state-machines.md`; permission/min-necessary: `../../11-permission-matrix.md`, `../../18-security-model.md`

## Guardrails
- Every stage shows minimum-necessary data only; provider queues get minimum beneficiary identity; identifier values are SPI.
- Registration never dead-ends on missing documents; eligibility is re-checked at every point of service.
- Consume-once and first-class partial dispensing must be reflected faithfully — never design a flow that could double-consume or lose an owed balance.
- Approval is a routed sub-flow with evidence + audit; no out-of-band/UI-only approvals; show approval status before gated fulfillment.
- Tag SMS/WhatsApp/QR/self-service/FHIR as [FUTURE]; launch channels are in-app + email + member number.
