# Phase 25 — five sponsor decisions: the pack, and the record of what was decided

**Prepared:** 2026-08-01 · **Status:** **RATIFIED as recommended, 2026-08-01** — see the decision record at the foot
**Subject:** the Branch Management build ([design 42](../../HBMP-Design/42-branch-management.md), [ADR-0029](../adr/0029-branch-management.md))
**Audience:** Mersal programme sponsor. **Overturning D2 or D5 would additionally need the DPO and the
Medical Director; ratifying as recommended needs neither.**

> **Correction, 2026-08-01.** This line previously read "D5 additionally needs the Medical Director",
> unqualified. That was wrong and it was wrong in the expensive direction — it would have held a decision
> that changes nothing clinically behind a signature it does not need. The Medical Director is required when
> D5 is **overturned** to clinic stock, because that puts administering a medicine outside the prescription
> path. Confirming *pharmacy* keeps it inside, which is the status quo.

---

## What was asked, and what was decided

Phase 25 gave the people who run Mersal's six clinics a workspace: practitioner roster, licence enforcement,
availability, and clinic stock. Five questions came up during design that are **not engineering choices** —
they set what the platform is allowed to do. Each was answered with a recommendation so the build could
proceed, and each answer was implemented and running while the question stayed open.

The sponsor was asked to **ratify or change** those five answers, and on **2026-08-01 ratified all five as
recommended**. This document is kept whole rather than rewritten: the sections below are what the decision was
taken *on*, and the record at the foot is what was decided. Each section says what was built, what the answer
is actually enforced by, and what changing it would have cost.

**No answer was changed, so no DPIA was triggered** and neither the DPO nor the Medical Director was required.
Only overturning D2 or D5 would have moved patient data into a system designed not to hold any.

> **One thing the pack got wrong, worth leaving visible.** It said ratifying as recommended required "no
> further work". That was true of D1–D4 and false of D5, whose gap had to be closed either way — and the pack
> said so itself two sections later. A summary line that contradicts the detail is the kind of error a
> decision-maker reads and the author never notices.

---

## Summary

| # | Question | Recommended | Enforced by | If you change it |
|---|---|---|---|---|
| D1 | Do clinics hold controlled substances? | **No, not in v1** | Database constraint | New module: weeks |
| D2 | Should stock use link to a patient visit? | **No** | Absence + 5 automated checks | Crosses the PHI boundary — **DPIA** |
| D3 | May a clinic coordinator create a practitioner record? | **Yes** | Unique licence number | Small — hours |
| D4 | Does the Clinics Manager get write access at all six? | **Yes** | Role reach + audit | Small — hours |
| D5 | Are vaccines clinic stock or pharmacy stock? | **Pharmacy** | Medicines-master check — see below | Crosses the PHI boundary — **DPIA** |

---

## D1 — Controlled substances are excluded from v1

**The question.** Should clinics be able to hold and record controlled drugs (morphine and similar) in the
new stock system?

**Recommended: no, not in this version.** A controlled register is not a category flag on an item. It needs
two signatures on every movement, a running balance tracked per individual ampoule, and reporting to the
Egyptian regulator in a prescribed form. That is a module of its own.

**What is built.** The database physically refuses to store an item marked as controlled. The screen also
refuses, with a message saying it is out of scope for this version rather than a technical error.

**Why a constraint rather than a rule.** If it were a setting, someone could enable it on a Tuesday
afternoon and the platform would begin holding controlled stock with none of the register, signatures or
reporting that the law requires. As a database constraint, turning it on is a deliberate code change that
someone must review.

**If you decide otherwise.** This is new work, not a switch — several weeks, and it should be scoped with
whoever is accountable for the controlled register at Mersal. Nothing built so far is wasted.

---

## D2 — Stock use is not linked to a patient visit

**The question.** When a clinic uses a box of gloves or a suture pack, should the system record which
patient it was used on?

**Recommended: no.** Stock use is recorded as a quantity at a clinic, not against a person.

**What is built.** There is no patient identifier anywhere in the stock system — not in the database, not in
any screen, not in any request it will accept. Five automated checks fail the build if one is ever added,
covering the database, the web requests, the data model and the running service.

**Why this matters more than it looks.** Two reasons, and the second is the one that bites:

1. **It keeps stock out of patient-data rules.** A storekeeper can use the system without being given access
   to clinical records. The moment stock rows name patients, the whole system inherits patient-data
   handling, retention and access rules.
2. **It keeps a second dispensing route from existing.** Anything given to a patient that needs a
   prescription goes through the pharmacy system, where eligibility, coverage limits, the approved-medicines
   list and the dispensing record are all enforced. If clinic stock could be issued *to a patient*, that
   becomes a way around every one of those controls — not deliberately, just by being easier.

**If you decide otherwise** (for example, to cost care per patient): this is a design change requiring a
DPIA, sign-off from the DPO, and rework of the stock system's data protection posture. It is not a column.
The honest alternative, if per-patient costing is the real need, is to derive it from the clinical record
that already exists rather than to make the storekeeping system hold patient data.

---

## D3 — A clinic coordinator may create a practitioner record

**The question.** When a locum doctor starts at a clinic, may the coordinator there create their record, or
must head office do it?

**Recommended: the coordinator may create it.** Central-only creation turns every new locum into a ticket to
head office, which in practice means the locum works while the paperwork waits.

**What is built.** Coordinators can create practitioner records. The guard is the medical licence number: it
is unique across the platform, enforced by the database. If a coordinator tries to create someone who
already exists, they are told who it is and offered "assign them to my clinic instead".

**Why the guard matters.** Six clinics can now each create a record, in good faith, without seeing each
other's roster. Without the licence check you would get three "Dr Hala Fouad" records and no way to say which
one holds the current licence or which one the appointments point at.

**If you decide otherwise.** Small change — remove the permission. Coordinators would then request new
practitioners from the network team.

---

## D4 — The Clinics Manager can make changes at all six clinics

**The question.** Should the person supervising all six clinics be able to change things everywhere, or only
view them and request changes?

**Recommended: change everywhere.** A supervisor who must raise a request to fix a rota is not supervising.

**What is built.** The Clinics Manager and the Branch Coordinator have **exactly the same permissions** —
they differ only in how many clinics they reach. Every change is audited, and the manager's screen makes the
target clinic explicit. Their reach comes from real, revocable clinic assignments, not from their job title.

**If you decide otherwise.** Small change — but be aware it splits the two roles apart. Today an automated
check fails the build if the coordinator and the manager ever get different permissions, which is what stops
them drifting. Making the manager read-only means maintaining two permission lists, and the one that gets
forgotten is always the narrower one.

---

## D5 — Vaccines are pharmacy stock, not clinic stock

> ### ✅ Closed, 2026-08-01 — this was the one with a live gap
>
> When this pack was written, D1–D4 were enforced by the platform and **D5 was enforced by nothing**: there
> was no reference to vaccines, injectables or any medicine identifier anywhere in the stock system, so
> nothing stopped someone cataloguing "Hepatitis B vaccine" as ordinary clinic stock.
>
> On ratification the check described below was built. **The stock catalogue now refuses any item that
> matches the medicines master**, naming the medicine it matched, and refuses it *also* when the medicines
> master cannot be reached — so the gate does not quietly open during an outage. The rule is now one the
> platform keeps rather than one people must remember.

**The question.** Vaccines and injectables sit between the two systems. Are they clinic stock (a consumable
used during care) or pharmacy stock (something dispensed against a prescription)?

**Recommended: pharmacy**, wherever a prescription or an authorisation applies. Clinic stock is consumables —
gloves, sutures, dressings, IV sets.

**What the gap actually risked.** The strict protection held throughout: even if a vaccine had been catalogued as
clinic stock, the system still could not issue it *to a named patient*, because no patient identifier exists
anywhere in it (D2). What would be lost is the **paperwork around giving it**: no prescription, no
eligibility check, no coverage limit, no entry in the dispensing record. The vaccine would be given, and the
controls that are supposed to surround giving it would simply have happened nowhere.

**What was built on the back of the decision.** Classification is now real rather than left to memory. When
someone adds an item to the clinic catalogue, the platform asks the medicines master whether it is a medicine,
and refuses it if so — naming which medicine it matched, so the person is told *why* rather than just *no*.

Three details worth knowing, because each was a choice:

- **It matches on more than an exact name.** Someone typing "Hepatitis B Vaccine 20mcg/ml vial" is caught by
  the master's "Hepatitis B Vaccine". The real mistake is never typed exactly, and a check that only caught
  exact spellings would have looked like protection without being any.
- **An outage refuses the item rather than admitting it.** If the medicines master cannot be reached, adding a
  catalogue item is declined with "retry shortly". The cost is that a new gauze SKU waits a few minutes; the
  alternative is an open gate during exactly the window nobody is watching.
- **There is no override tick-box, deliberately.** An override is how a decision that must be made once,
  centrally, becomes six clinics each ticking a box on a Tuesday. If a genuine consumable is ever refused, the
  medicines master is what gets corrected — and that correction is visible to the people accountable for it.

**Had you decided clinic stock instead**, this would have needed a DPIA and Medical Director sign-off, because
it puts administering a medicine outside the prescription path — a substantially larger change than it sounds.

---

## What was at stake in not deciding

Kept for the record, because it is why the pack was written rather than left as a backlog item. The costs
were cumulative: D5's gap would have stayed open and unguarded; D2 would have got harder to reverse with every
week of live use (after go-live it is a data migration plus a retrospective DPIA over records already
collected, not a design change to something not yet in use); and `BUILD-STATUS.md` would have carried five
decisions marked provisional, which is honest but leaves anyone planning work with five open questions.

---

## Decision record

**All five ratified as recommended on 2026-08-01.** No answer was changed, so neither the DPO nor the Medical
Director block below applies, and no DPIA was triggered — the recommended answers are the conservative ones on
every count and none of them moves patient data.

| # | Decision | Ratified as recommended? | If changed, what instead | Decided by | Date |
|---|---|---|---|---|---|
| D1 | Controlled substances excluded from v1 | **Yes** | — | Programme sponsor *(name below)* | 2026-08-01 |
| D2 | Stock use not linked to a patient | **Yes** | — | Programme sponsor *(name below)* | 2026-08-01 |
| D3 | Coordinator may create a practitioner | **Yes** | — | Programme sponsor *(name below)* | 2026-08-01 |
| D4 | Clinics Manager writes at all six | **Yes** | — | Programme sponsor *(name below)* | 2026-08-01 |
| D5 | Vaccines are pharmacy stock | **Yes** | — | Programme sponsor *(name below)* | 2026-08-01 |

**The one field left open — and it is left open on purpose.** The decision above was given by the programme
owner and is recorded exactly as given. The printed name and role are not, because nobody but the signatory
can supply those, and a governance record that invents a name is worth less than one that admits a blank:

| | |
|---|---|
| **Printed name** | |
| **Role** | |
| **Signature / date** | |

**The DPO and Medical Director block stays empty, and that emptiness is itself the record** — it is the
evidence that no decision crossing the PHI boundary was taken:

| Role | Name | Date |
|---|---|---|
| Data Protection Officer | *n/a — no answer changed* | |
| Medical Director | *n/a — no answer changed* | |

### What followed from this table

- [ADR-0029](../adr/0029-branch-management.md) moved from *Accepted (with five provisional decisions)* to
  **Accepted**.
- `docs/BUILD-STATUS.md` dropped the provisional note under 25.0.
- [design 42 §8](../../HBMP-Design/42-branch-management.md) records the outcome against each question.
- **D5's enforcement gap was closed** — see D5 above for what was built.
