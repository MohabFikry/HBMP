# 03 — User Personas

> Cluster A · Product & Discovery
> Up: [00-README-INDEX.md](00-README-INDEX.md) · Foundations: [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [01-product-vision.md](01-product-vision.md) · [02-stakeholder-analysis.md](02-stakeholder-analysis.md) · [04-patient-journey-maps.md](04-patient-journey-maps.md)

---

## How to use these personas

Each persona turns a stakeholder class from [02-stakeholder-analysis.md](02-stakeholder-analysis.md) into a concrete working human, so design and engineering can reason about real goals, contexts, and constraints. Every persona lists a **Must-see / Must-not-see** data boundary — this is the persona-level expression of the data-minimization and least-privilege principles in [0A §2/§7](0A-DESIGN-FOUNDATIONS.md), and it is the input to the formal [10-role-matrix.md](10-role-matrix.md) and [11-permission-matrix.md](11-permission-matrix.md). Accessibility notes feed [21-accessibility-checklist.md](21-accessibility-checklist.md). Devices/context feed responsive and offline-tolerance design.

Personas are grouped: **A. Internal staff** (11) · **B. External provider operators** (3) · **C. Beneficiaries** (2). Total: 16.

> Names are illustrative. "Patient" is used as the UI-facing synonym for **Beneficiary** in clinical contexts only ([0A §2](0A-DESIGN-FOUNDATIONS.md)).

---

# A. Internal staff personas

## A1 — Rania, Registration / Beneficiary Management Officer

- **Role & context:** Front desk at a Mersal intake point. Registers newly-arrived beneficiaries and re-verifies returnees. Shared desktop workstation, document scanner, busy waiting area, high daily volume.
- **Goals:** Register a beneficiary correctly and quickly; avoid creating duplicates; issue a member identity on the spot.
- **Key tasks:** Capture identity from any document type (National ID, Passport, Refugee ID, UNHCR number); scan/upload documents; run identity de-duplication match; create/activate beneficiary + member; hand over member number.
- **Frustrations (today):** Re-keying the same details; duplicate records for returnees who present a different document; illegible paper; can't tell if someone already exists.
- **Must-see:** Identity/demographic fields, document capture, duplicate-match candidates, member status.
- **Must-not-see:** Clinical/EMR content, other beneficiaries' full records beyond match-disambiguation minimum, financial data.
- **Accessibility:** Arabic-first RTL; large touch targets; fast keyboard entry; scanner-assisted capture; works on a modest shared PC.
- **Quote:** *"If she was here last year with a passport and today with a refugee card, I need the system to know it's the same person — not make a second file."*

## A2 — Karim, Call Center Agent

- **Role & context:** Handles inbound beneficiary calls. Headset + desktop, high call volume, needs answers in seconds while the caller waits.
- **Goals:** Identify the caller, answer status/eligibility questions, book or route them, all in one call.
- **Key tasks:** Look up beneficiary by any identifier; read eligibility status; check/create appointments; escalate or route; log the interaction.
- **Frustrations:** Searching across spreadsheets and phone chains; can't confirm eligibility live; no single caller view.
- **Must-see:** Identity match, eligibility status, appointment schedule, high-level status flags.
- **Must-not-see:** Detailed clinical notes, prescription contents (beyond existence/status if needed for scheduling), financials.
- **Accessibility:** Fast search with typo tolerance (bilingual name spellings); keyboard-first; screen-reader-friendly; RTL/LTR.
- **Quote:** *"The caller can't wait while I open four spreadsheets. One search, one screen, one answer."*

## A3 — Mona, Appointment Coordinator

- **Role & context:** Appointment Team. Schedules beneficiaries into clinics/providers and manages the calendar. Desktop, sometimes juggling multiple providers.
- **Goals:** Book eligible beneficiaries into available, appropriate slots without conflicts; manage reschedules and no-shows.
- **Key tasks:** Search availability across providers; gate booking on eligibility; schedule/reschedule/cancel; trigger reminders (in-app/email now; **SMS/WhatsApp future**).
- **Frustrations:** Double-booking; booking ineligible beneficiaries; manual reminders; no-show churn.
- **Must-see:** Provider availability, eligibility status, appointment history, referral context.
- **Must-not-see:** Full clinical notes; financial detail; unrelated providers' internal data.
- **Accessibility:** Calendar with keyboard navigation and non-color status cues; RTL-aware date/time; clear conflict warnings.
- **Quote:** *"I shouldn't be able to book someone who isn't eligible — the system should stop me before I waste their trip."*

## A4 — Dr. Hossam, Doctor (Clinical Consultation)

- **Role & context:** Sees beneficiaries in a network clinic. Time-pressured, back-to-back consults. Desktop or tablet; sometimes shared device.
- **Goals:** Understand the patient quickly, document the encounter, order investigations and prescribe safely.
- **Key tasks:** Open the longitudinal record; review history, allergies, active meds, prior results; write SOAP notes; create investigation orders and prescriptions; make referrals; flag items needing approval.
- **Frustrations:** Blind consultations with no history; illegible prior notes; ordering duplicate tests; no allergy/interaction visibility.
- **Must-see (for their encounter):** Beneficiary clinical record scoped to care — history, allergies, active meds, prior results, current encounter, eligibility/coverage for what they order.
- **Must-not-see:** Records of beneficiaries not under their care; financial/administrative internals beyond coverage relevance; other providers' internal operations.
- **Accessibility:** Efficient point-of-care entry; allergy/interaction alerts with icon+text (never color-only); works on modest tablet; RTL/LTR; minimal clicks.
- **Quote:** *"Show me her allergies and what she's already on before I write anything — that's the difference between safe and sorry."*

## A5 — Nurse Salma, Clinical Nurse

- **Role & context:** Triage and support within a clinic. Captures vitals, prepares the patient, supports the doctor. Shared tablet/desktop, on her feet.
- **Goals:** Capture vitals and triage data fast; keep the encounter moving; not duplicate work.
- **Key tasks:** Record vitals and triage notes; verify identity; prep the encounter; view care-relevant record; support order/sample handling.
- **Frustrations:** Paper vitals re-entered later; no structured capture; unclear who the next patient is.
- **Must-see:** Care-relevant clinical fields for the current encounter; vitals; triage; allergies.
- **Must-not-see:** Full physician-only notes beyond need; financials; unrelated beneficiaries.
- **Accessibility:** Very fast numeric entry; large targets; glove-friendly; RTL/LTR; minimal typing.
- **Quote:** *"Let me put the vitals in once, here, and have them just be there for the doctor."*

## A6 — Yasmin, Medical Approval Reviewer

- **Role & context:** Medical Approval Team. Reviews requests for high-cost/controlled services (e.g., MRI, expensive drugs) against policy. Desktop; queue-driven; TAT-sensitive.
- **Goals:** Make consistent, defensible approve/partial/reject/info-request decisions fast, with evidence.
- **Key tasks:** Work the approval queue; review attached clinical evidence and coverage; apply policy rules; decide with a recorded rationale; request more info; escalate.
- **Frustrations:** Approvals arriving by phone/photo; inconsistent decisions; no record; no evidence attached; no TAT visibility.
- **Must-see:** The authorization request, attached clinical evidence, coverage/limits, beneficiary identity minimum, policy rules, history of similar decisions.
- **Must-not-see:** Unrelated clinical detail beyond what justifies the decision (minimum necessary); other reviewers' unrelated queues.
- **Accessibility:** Queue with clear status (icon+shape+text per [0A §5.2](0A-DESIGN-FOUNDATIONS.md)); keyboard-driven decisions; TAT indicators; RTL/LTR.
- **Quote:** *"Give me the evidence and the rule in one place, and let me record exactly why I said yes or no."*

## A7 — Dr. Adel, Medical Director

- **Role & context:** Clinical governance and oversight. Sets policy, handles escalations and overrides, watches cost/quality. Desktop; also reviews on the move.
- **Goals:** Enforce clinical and spend policy; oversee approvals and outcomes; intervene where needed; report to leadership.
- **Key tasks:** Configure/adjust policy and formulary posture; review escalations; exercise override/emergency-approval authority; monitor dashboards; audit decisions.
- **Frustrations:** No visibility into spend/quality/patterns; can't enforce policy consistently; approvals invisible.
- **Must-see:** Oversight dashboards, approval analytics, escalations, policy config, audit trail; scoped clinical detail on escalation.
- **Must-not-see:** Bulk clinical records without a governance reason (access is logged); anything outside oversight/escalation mandate.
- **Accessibility:** Dashboards with non-color-only encodings; drill-down; export; RTL/LTR; readable on tablet.
- **Quote:** *"I can't govern what I can't see — and every override I make must leave a trail."*

## A8 — Nadia, Case Manager

- **Role & context:** Coordinates complex and chronic beneficiaries across the whole journey. Desktop; longitudinal, relationship-based work.
- **Goals:** Keep continuity for vulnerable beneficiaries; coordinate across phases and providers; ensure nothing is dropped.
- **Key tasks:** View the beneficiary timeline across all phases; track follow-ups and tasks; coordinate approvals, appointments, meds; flag risks; advocate for the beneficiary.
- **Frustrations:** Fragmented care; no continuity; manual coordination across disconnected fragments.
- **Must-see:** The longitudinal cross-phase timeline for their managed beneficiaries; tasks; approvals; appointments; care-relevant clinical summary.
- **Must-not-see:** Beneficiaries outside their caseload without cause; deep financial internals.
- **Accessibility:** Timeline view with clear chronology and status cues; task management; RTL/LTR; screen-reader-friendly.
- **Quote:** *"For my chronic patients, the whole point is the thread — one view where I can see everything that's happened and what's next."*

## A9 — Tarek, Finance Officer

- **Role & context:** Finance team. Tracks funded-service spend, utilization, and reporting. Desktop; spreadsheet-heavy background; needs trustworthy exports.
- **Goals:** Know what was authorized vs. delivered; report spend and utilization; support donor/leadership reporting; enforce cost controls.
- **Key tasks:** Run utilization/approval/spend reports; reconcile authorized-vs-delivered; export for GL/donor accounting (HBMP does not replace ERP); flag anomalies.
- **Frustrations:** No line of sight into funded spend; manual reconciliation; can't prove stewardship.
- **Must-see:** Aggregated and line-level authorization/utilization/spend data; provider/service breakdowns; coverage usage.
- **Must-not-see:** Detailed clinical notes beyond what a cost line requires (minimum necessary); beneficiary identity beyond reporting need.
- **Accessibility:** Reports/exports with accessible tables; non-color-only encodings; RTL/LTR numerals & date formats.
- **Quote:** *"A donor will ask how much we spent on scans and whether they were approved — I need to answer with data, not guesses."*

## A10 — Sherif, Network / Provider Admin

- **Role & context:** Network Team + Provider Admin duties. Onboards and manages contracted providers and their portal users; configures isolation. Desktop.
- **Goals:** Maintain an accurate provider directory; onboard providers and their users; enforce provider isolation and scope.
- **Key tasks:** Create/manage providers and contract/coverage terms; provision provider portal users; set scopes and queues; deactivate; monitor provider performance.
- **Frustrations:** Manual provider coordination; no central directory; no isolation tooling; ad hoc access.
- **Must-see:** Provider directory, contracts/coverage terms, provider user accounts, scope/isolation config, provider performance.
- **Must-not-see:** Beneficiary clinical records; approval decisions' clinical detail; unrelated internal admin functions.
- **Accessibility:** Admin forms with clear validation; least-privilege UX; RTL/LTR.
- **Quote:** *"When I onboard a lab, they should see their orders and only their orders — that boundary is my job."*

## A11 — Layla, Super Admin

- **Role & context:** Platform administration. Configures roles, permissions, and system settings; manages break-glass; guards least privilege. Desktop; highly audited.
- **Goals:** Keep the platform correctly configured and secure without over-granting access; manage exceptional access safely.
- **Key tasks:** Manage roles/permissions (RBAC/ABAC); configure system settings and tenants; oversee break-glass/emergency access; review access; support other admins.
- **Frustrations (greenfield):** No existing system; everything currently manual; risk of over-broad admin power.
- **Must-see:** Configuration surfaces, role/permission model, system health, audit of admin actions.
- **Must-not-see:** Clinical/financial *content* by default — admin power is over configuration, not data; any data access is explicit, justified, and logged (break-glass).
- **Accessibility:** Powerful but guard-railed admin UI; confirmation and audit on sensitive actions; RTL/LTR.
- **Quote:** *"Admin should mean I can configure the system — not that I can quietly read everyone's medical history."*

---

# B. External provider-operator personas

## B1 — Fatma, Laboratory Technician

- **Role & context:** Works at a contracted lab (external, isolated). Receives investigation orders, runs tests, uploads results. Desktop/tablet at the lab bench; provider-isolated portal.
- **Goals:** See her lab's incoming orders, fulfill them once, upload results, get it off the queue.
- **Key tasks:** View the lab's order queue; verify beneficiary/order minimum; **consume** an order line atomically (consume-once, [0A §7](0A-DESIGN-FOUNDATIONS.md)); upload result documents; mark complete.
- **Frustrations:** Paper orders; uncertain eligibility; phone chasing; risk of running a test twice or for the wrong person.
- **Must-see:** Only her lab's queue; the specific order and the minimum beneficiary identity needed to run it safely; result-upload.
- **Must-not-see:** Other providers' queues; beneficiary clinical history beyond the order; financials; any beneficiary not in her queue.
- **Accessibility:** Minimal, fast task screens; clear consume/complete actions; non-color-only status; RTL/LTR; modest hardware.
- **Quote:** *"I just need my orders, the right patient, and a button that says 'this one's mine now' so nobody runs it twice."*

## B2 — Amir, Pharmacist

- **Role & context:** At a contracted pharmacy (external, isolated). Dispenses prescriptions within coverage, including partial dispensing. Desktop; provider-isolated portal; sometimes stock-limited.
- **Goals:** Dispense the right medication to the right beneficiary within coverage, record partials accurately, once.
- **Key tasks:** View the pharmacy's prescription queue; verify coverage; **dispense** fully or partially (records `PartiallyDispensed`/`Dispensed`, [0A §6](0A-DESIGN-FOUNDATIONS.md)); handle substitution within policy; complete.
- **Frustrations:** Paper scripts; no coverage certainty; can't record partial dispensing; risk of double-dispensing.
- **Must-see:** Only the pharmacy's prescription queue; the prescription and minimum beneficiary identity; coverage/limits for the item.
- **Must-not-see:** Full clinical record; other pharmacies' queues; unrelated beneficiaries; financial internals beyond coverage.
- **Accessibility:** Fast dispense workflow; clear partial-dispense capture; interaction/allergy flags where in scope; non-color-only status; RTL/LTR.
- **Quote:** *"When I only have half the quantity, I need to record exactly that — and the record has to know the rest is still owed."*

## B3 — Omar, Imaging Technician

- **Role & context:** At a contracted imaging center (external, isolated). Performs radiology, including high-cost studies that require prior approval. Desktop; provider-isolated portal.
- **Goals:** Perform approved imaging for the right beneficiary, confirm approval status first, upload the report.
- **Key tasks:** View the imaging queue; **check approval status** on high-cost studies (Phase 7); consume the order; perform; upload report/images metadata; complete.
- **Frustrations:** Paper orders; approval confirmed by phone; no status visibility; performing before approval and not getting reimbursed.
- **Must-see:** Only his center's queue; the order plus its approval status; minimum beneficiary identity; result/report upload.
- **Must-not-see:** Clinical history beyond the order; other providers' queues; approval *rationale* internals beyond status; financials.
- **Accessibility:** Clear approval-status indicator (icon+shape+text); consume-once action; RTL/LTR; modest hardware.
- **Quote:** *"Before I run an MRI I have to know it's approved — I can't rely on a phone call that might be about someone else."*

---

# C. Beneficiary personas

> Beneficiaries are **not v1 system users** (no self-service app in MVP — see [28-mvp-definition.md](28-mvp-definition.md)). These personas exist to keep the journey human-centered; they are served *through* staff and providers. They have the highest interest and lowest formal influence ([02 §2.3](02-stakeholder-analysis.md)), so their needs are encoded as design constraints.

## C1 — Abdullah, Newly-Arrived Refugee Family Head

- **Role & context:** Recently arrived; registering himself and his family for the first time. Limited/partial documentation; Arabic-speaking (possibly a dialect); variable literacy; may be anxious and unfamiliar with the process. No device of his own in the flow; served at the desk.
- **Goals:** Get his family registered and receive first care, despite incomplete papers; be treated with dignity; understand what's happening.
- **Key tasks (as served):** Present whatever identity documents he has; answer intake questions; receive a member identity; be guided to first care.
- **Frustrations:** Documentation gaps that risk turning him away; language barrier; unfamiliar, intimidating process; fear of his data being misused.
- **Data — must be handled for him:** Only the minimum identity/demographic needed to establish membership and safe care; his sensitive status protected by data minimization and least privilege.
- **Data — must-not happen:** His information exposed beyond need; his story re-collected repeatedly; his refugee status visible to anyone without cause.
- **Accessibility (designed for, via staff-facing screens):** Arabic-first RTL; document-flexible identity ([0A §3](0A-DESIGN-FOUNDATIONS.md)); low-literacy-aware, plain-language guidance; dignity in framing; no dead-ends when documents are incomplete.
- **Quote:** *"I don't have all the papers I used to. I just need someone to see my family and help — and I need to trust where this information goes."*

## C2 — Um Yusuf, Chronic-Illness Beneficiary

- **Role & context:** Established beneficiary managing an ongoing condition (e.g., diabetes/hypertension) requiring recurring visits, medications, and periodic investigations — some high-cost. Returns often, sometimes to different providers. Arabic-speaking; served through staff/providers.
- **Goals:** Continuity — a record that follows her, uninterrupted medication, timely approvals, no repeated tests; to be recognized each time.
- **Key tasks (as served):** Be reliably matched to her existing record on return; continue medications and monitoring; have high-cost investigations approved without delay; be coordinated by a Case Manager.
- **Frustrations:** Fragmented history across providers; interrupted medication continuity; repeat tests because prior results aren't visible; delays in approvals for needed scans.
- **Data — must be surfaced (to the right role):** Her longitudinal record — history, allergies, active meds, prior results — to clinicians treating her and to her Case Manager, so care is continuous and safe.
- **Data — must-not happen:** Her chronic condition or history exposed to providers/roles outside her care; duplicate records fragmenting her again.
- **Accessibility (designed for):** Continuity-first design (identity matching, longitudinal record); Arabic RTL; clear medication/appointment continuity; approval TAT that respects chronic need.
- **Quote:** *"Every time I come back I shouldn't have to start over. My body's story is the same — the system should remember it, even if I see a different doctor."*

---

## Persona → role & data-boundary summary

Quick cross-reference. Formal, enforceable definitions live in [10-role-matrix.md](10-role-matrix.md) / [11-permission-matrix.md](11-permission-matrix.md); this is the design intent.

| Persona | Internal role | Journey phase(s) | Clinical data access | Financial data access | Provider-isolated? |
|---------|---------------|------------------|----------------------|-----------------------|:---:|
| A1 Rania — Registration | Registration/Beneficiary Mgmt | 1 | None (identity/demographic only) | None | n/a |
| A2 Karim — Call Center | Call Center | 2,3 | Status only | None | n/a |
| A3 Mona — Appointments | Appointment Team | 3 | Minimal (referral context) | None | n/a |
| A4 Dr. Hossam — Doctor | Doctor | 4 | Full, scoped to care | Coverage-relevant only | n/a |
| A5 Salma — Nurse | Nurse | 4 | Care-relevant, scoped | None | n/a |
| A6 Yasmin — Approval Reviewer | Medical Approval Team | 7 | Evidence-scoped | Coverage/limits | n/a |
| A7 Dr. Adel — Medical Director | Medical Directors | 4,7 (oversight) | Oversight + escalation-scoped | Cost/spend analytics | n/a |
| A8 Nadia — Case Manager | Case Managers | 1–7 (caseload) | Longitudinal, caseload-scoped | Minimal | n/a |
| A9 Tarek — Finance | Finance | reporting | Minimal (cost-line only) | Full (aggregate + line) | n/a |
| A10 Sherif — Network/Provider Admin | Network Team/Provider Admin | network mgmt | None | Contract/coverage terms | configures isolation |
| A11 Layla — Super Admin | Super Admin | platform | None by default (break-glass, logged) | None by default | manages tenancy |
| B1 Fatma — Lab Tech | External (Lab) | 5 | Order-scoped only | None | **Yes** |
| B2 Amir — Pharmacist | External (Pharmacy) | 6 | Prescription + coverage-scoped | Coverage only | **Yes** |
| B3 Omar — Imaging Tech | External (Imaging) | 5,7 | Order + approval-status-scoped | None | **Yes** |
| C1 Abdullah — New refugee | Beneficiary (served) | 1,2 | Own (via staff) | n/a | n/a |
| C2 Um Yusuf — Chronic | Beneficiary (served) | 1–7 (via staff) | Own longitudinal (via care team) | n/a | n/a |

---

## Cross-cutting accessibility commitments (all personas)

Per [0A §5](0A-DESIGN-FOUNDATIONS.md) and [21-accessibility-checklist.md](21-accessibility-checklist.md), every persona's UI must satisfy: full **Arabic RTL + English LTR** with mirrored layout; **WCAG 2.2 AA** contrast; status conveyed by **color + icon + shape + text** (never color alone); **44×44px** minimum targets; visible 3px focus rings; keyboard operability; screen-reader support; and tolerance for **modest, shared devices and imperfect connectivity** in the field. SMS/WhatsApp notification channels and QR-based handoffs referenced in the journeys are **future**, not launch.

---

*Continue: [04-patient-journey-maps.md](04-patient-journey-maps.md) places these personas into the end-to-end journey.*
