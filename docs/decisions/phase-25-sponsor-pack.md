# Phase 25 — five decisions needing sponsor sign-off

**Prepared:** 2026-08-01 · **Status:** awaiting decision — **nothing here is signed off**
**Subject:** the Branch Management build ([design 42](../../HBMP-Design/42-branch-management.md), [ADR-0029](../adr/0029-branch-management.md))
**Audience:** Mersal programme sponsor. **D5 additionally needs the Medical Director; overturning D2 or D5
additionally needs the DPO.**

---

## What is being asked

Phase 25 gave the people who run Mersal's six clinics a workspace: practitioner roster, licence enforcement,
availability, and clinic stock. Five questions came up during design that are **not engineering choices** —
they set what the platform is allowed to do. Each was answered with a recommendation so the build could
proceed, and each answer is **implemented and running**.

You are being asked to **ratify or change** those five answers. This document says, for each one, what is
built today, what it is actually enforced by, and what changing it would cost now versus after go-live.

**Ratifying all five as recommended requires no DPIA and no further work.** Only overturning D2 or D5 moves
patient data into a system designed not to hold any, and that is a different kind of change.

---

## Summary

| # | Question | Recommended | Enforced by | If you change it |
|---|---|---|---|---|
| D1 | Do clinics hold controlled substances? | **No, not in v1** | Database constraint | New module: weeks |
| D2 | Should stock use link to a patient visit? | **No** | Absence + 5 automated checks | Crosses the PHI boundary — **DPIA** |
| D3 | May a clinic coordinator create a practitioner record? | **Yes** | Unique licence number | Small — hours |
| D4 | Does the Clinics Manager get write access at all six? | **Yes** | Role reach + audit | Small — hours |
| D5 | Are vaccines clinic stock or pharmacy stock? | **Pharmacy** | ⚠️ **nothing** — see below | Crosses the PHI boundary — **DPIA** |

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

> ### ⚠️ This is the one that needs action whichever way you decide
>
> D1–D4 are enforced by the platform. **D5 is not enforced by anything.** There is no reference to vaccines,
> injectables or any medicine identifier anywhere in the stock system. Nothing today stops someone
> cataloguing "Hepatitis B vaccine" as ordinary clinic stock.

**The question.** Vaccines and injectables sit between the two systems. Are they clinic stock (a consumable
used during care) or pharmacy stock (something dispensed against a prescription)?

**Recommended: pharmacy**, wherever a prescription or an authorisation applies. Clinic stock is consumables —
gloves, sutures, dressings, IV sets.

**What the gap actually risks.** The strict protection still holds: even if a vaccine were catalogued as
clinic stock, the system still could not issue it *to a named patient*, because no patient identifier exists
anywhere in it (D2). What would be lost is the **paperwork around giving it**: no prescription, no
eligibility check, no coverage limit, no entry in the dispensing record. The vaccine would be given, and the
controls that are supposed to surround giving it would simply have happened nowhere.

**What we recommend regardless of the decision.** Classification should be made real rather than left to
memory:

- **If you confirm pharmacy** — add a check so a catalogue item cannot be created against something in the
  medicines master. Roughly a day's work, and it turns a rule people must remember into one the platform
  keeps.
- **If you decide clinic stock** — this needs a DPIA and Medical Director sign-off, because it puts
  administering a medicine outside the prescription path. It is a substantially larger change than it sounds.

**Either way, the classification should be made once, centrally.** Deciding it per clinic is how the same
vial ends up governed two different ways in two branches.

---

## If no decision is made

Nothing breaks. The platform runs on the recommended answers, which are the conservative ones on every
count — they hold data in, not out.

The costs of leaving it are these, and they are cumulative:

- **D5's gap stays open**, and stays unguarded. It is the only one with a live risk attached.
- **The recommended answers get harder to reverse over time.** D2 in particular: reversing it after clinics
  have been using the system means a data migration and a retrospective DPIA over records already collected,
  rather than a design change to something not yet in use.
- **`BUILD-STATUS.md` continues to carry five decisions marked provisional**, which is honest but means
  anyone reading it to plan work has five open questions in front of them.

---

## Decision record

*To be completed by the sponsor. Leave a row blank rather than guessing at it — an unrecorded decision is
recoverable; a wrongly recorded one is not.*

| # | Decision | Ratified as recommended? | If changed, what instead | Decided by | Date |
|---|---|---|---|---|---|
| D1 | Controlled substances excluded from v1 | | | | |
| D2 | Stock use not linked to a patient | | | | |
| D3 | Coordinator may create a practitioner | | | | |
| D4 | Clinics Manager writes at all six | | | | |
| D5 | Vaccines are pharmacy stock | | | | |

**Additional signatures required only if D2 or D5 is changed:**

| Role | Name | Date |
|---|---|---|
| Data Protection Officer | | |
| Medical Director | | |

Once completed, this table is the source of truth. [ADR-0029](../adr/0029-branch-management.md) moves from
provisional to Accepted, `docs/BUILD-STATUS.md` drops the provisional note under 25.0, and
[design 42 §8](../../HBMP-Design/42-branch-management.md) records the outcome against each question.
