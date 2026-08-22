# 55 — A Roster Nobody Could Read

> **Status:** implemented (2026-08-22).
> **Reads on:** [42](42-branch-operations.md) §1/§4/§6/§7, [37](37-branch-scoping.md) §3,
> [12](12-ui-wireframes.md), [13](13-ux-flows.md), [14](14-navigation-structure.md),
> [0B](0B-DESIGN-SYSTEM-UI.md) §4/§10b, [21](21-accessibility-checklist.md), ADR-0029.
> **Found by:** being asked to redesign Roster & Availability, and finding that the screen could not
> answer either of the two questions a clinic asks it.

---

## 1. The screen was a table of rules

Roster & Availability opened with a sentence — *"the weekly pattern says when the clinic normally runs and
how many patients each clinician takes"* — and then printed one row per **availability rule**:

| Clinician | Day | Hours | Slot length | Daily limit | Slots offered |
|---|---|---|---|---|---|
| Karim Adel | Monday | 14:00–18:00 | 20 min | No limit | 12 |
| Karim Adel | Wednesday | 14:00–17:00 | 20 min | 8 | 8 |
| Karim Adel | Thursday | 14:00–18:00 | 20 min | No limit | 12 |

A clinician working five days appeared five times. There was no clinic column, so the Wednesday row above —
which is at **Dokki**, not Maadi — was indistinguishable from the two beside it. Sorted by day (the only
sortable column that mattered), one person's week was scattered through the table.

That layout answers a question nobody asks. The two that are asked are:

1. **"What does Dr Karim's week look like?"** — the question behind every change to hours, slot length or
   capacity, and behind every request for cover.
2. **"Who is in at Dokki on Thursday?"** — the question at the start of every clinic day.

The first was answerable only by reading the whole table and filtering by eye. The second was not answerable
at all, and §3 is about why.

### 1.1 What was under it

The data was fine. `emr.provider_availability` has carried the weekly rule since 0002, `emr.roster_exception`
has carried the dated departures from it since 25.4, and both have full CRUD, history triggers and impact
previews. Nothing below the screen needed changing. The screen was showing the storage shape.

---

## 2. The redesign

Two views behind a segmented control, and the exception calendar folded behind a button.

### 2.1 Weekly pattern — master/detail over people

A filter bar (clinic, when the caller runs more than one; clinician, as a typeahead), a list of **unique**
clinicians, and their week in a pane beside it.

```
┌─ Filters ──────────────────────────────────────────────────────────┐
│  Clinic [ All clinics you run ▾ ]   Clinician [ Search by name… ▾ ] │
└────────────────────────────────────────────────────────────────────┘
┌─ Clinicians ─────────────────┐ ┌─ Karim Adel ───────────────────────┐
│ Clinician   Clinics    Days  │ │ Doctor · Cardiology                │
│ Hala Fouad  Maadi      2     │ │ Assigned clinics [Maadi] [Dokki]   │
│ Karim Adel  Maadi·Dokki 3    │ │ ─────────────────────────────────  │
│ Mona Saleh  Maadi   No pattern│ │ Sunday    — Not working      Add  │
└──────────────────────────────┘ │ Monday   Maadi 14:00–18:00 … Edit  │
                                 │ Tuesday   — Not working      Add   │
                                 │ Wednesday Dokki 14:00–17:00 … Edit │
                                 └────────────────────────────────────┘
```

Four decisions worth stating:

**The list is built from the practitioner directory, not from the rules.** A clinician with no pattern is
exactly who somebody is looking for when a clinic is short, and a list derived from availability would not
contain them. Mona Saleh's row says **No pattern** rather than being absent.

**All seven days, always.** The days somebody does *not* work are the answer to "when could they cover?", and
a table of only their sessions cannot show one. A weekday with two rules — two clinics on one day — expands to
two rows rather than collapsing, because they are two sessions in two buildings.

**The clinic is named wherever a row could be ambiguous.** On the list, for anyone at more than one; on the
pattern rows, for the same people. A column repeating one word seven times is noise on the narrower half of a
split layout, so it appears only where it distinguishes something.

**Editing covers what a clinics manager actually changes**: the assigned clinics, the hours, the slot length,
and the daily cap — plus adding and removing a working day, because a pane that can narrow a Tuesday and not
retire it leaves the only route to "stop working Tuesdays" as an exception, which is the one thing exceptions
must not be used for (§2.4).

### 2.2 Today's roster — one clinic, one date

```
Clinic [ Maadi ▾ ]  Date [ 2026-08-27 ]  ‹ Previous | Today | Next ›

┌ 3 on duty ┐ ┌ 32 offered ┐ ┌ 11 booked ┐ ┌ 21 open ┐

⚠ In force on this date
   Leave · Annual leave · Hala Fouad

Clinician    Hours        Slot   Limit  Offered  Booked  Open  Status
Hala Fouad   09:00–13:00  15min  12     0        0       0     ⚠ Off · Annual leave
Karim Adel   14:00–18:00  20min  —      12       7       5     ✓ Working
Nadia Rashed 10:00–14:00  30min  —      6 / 8    4       2     ✓ Working · Shortened — Hospital round
```

**A clinician who is away keeps their line.** "Dr Hala is not on today's roster" and "Dr Hala is on annual
leave" are the same screen to somebody ringing round for cover, and only one of them says what to do next. So
the line stays, the status says `Off`, and the reason travels with it.

**Both numbers when they disagree.** `6 / 8` says something took two away; `6` alone looks like the pattern.
The status cell beside it says what.

**A day with no lines still explains itself.** A closure on a date nobody was rostered removes nothing, and
without the notices band a public holiday and a rota somebody forgot to enter read identically.

### 2.3 The exceptions, folded

The exception table (ninety days) and its nine-field record form used to occupy roughly two thirds of the
page and were always open. Both are **maintenance** — somebody records an absence when it becomes known — and
neither is what anyone opens the roster to read. They pushed the weekly pattern below the fold and left no
room at all for a day view.

They now live behind an **Exceptions (2)** button, in a wide dialog. The count on the button is what the table
used to say by being visible. The dialog is rendered only while open, so the impact preview inside it starts
clean: a preview is a claim about a moment, and one left over from a dialog closed twenty minutes ago is
precisely the stale number the acknowledgement step exists to prevent.

The button is **not** in `PageHeader`. That component renders nothing when there is no session — a reasonable
guard for something whose entire content is derived from the caller's portal, and a bad place for the only
route to a feature. It sits on the toolbar beside the view switch, where it is visible in both views.

### 2.4 What did not change

The rule that adding an exception leaves the weekly pattern intact, and that removing a working day to cover
one absence removes every other week too. It is stated on the removal confirmation and again in the exceptions
dialog, because the two are the places somebody is about to get it wrong.

---

## 3. The day roster is computed where availability is

`GET /api/v1/roster/day?branchId=&date=` (emr, `appointment:read`, branch-scoped).

The screen already held the weekly pattern and the exception calendar. Combining them in the browser would
have been perhaps forty lines, and it would have been a second implementation of four rules that live in
`SlotGeneration`:

* a whole-day subtraction removes the day outright — **including any extra clinic on it**, because an extra
  session at a shut clinic is not a session, and the other ordering lets a stale ad-hoc row quietly reopen a
  building somebody closed;
* a part-day absence removes the slots it **overlaps** — a slot half inside a leave window is not half-bookable;
* the daily cap applies **across every window the date offers** and **after** subtraction, so a cap of twenty
  on a day when leave removes the afternoon yields the morning's slots rather than the first twenty of a
  session that is not happening;
* a trailing partial slot is not a slot.

Design 42 §7 rule 5 says availability is computed in exactly one place. A copy of these four rules in
TypeScript, with no tests over any of them, would diverge — and the first divergence is a clinic telling a
patient it is open on a day the booking engine has already closed. So the endpoint runs `SlotGeneration` for a
single date and returns the answer.

### 3.1 The response

```
{ date, branchId,
  lines:   [{ practitionerId, branchId, startTime, endTime, slotMinutes, maxPerDay,
              slotsFromPattern, slotsOffered, booked,
              status: "Working" | "Off" | "Extra", exceptionKind, exceptionReason }],
  notices: [{ exceptionId, kind, reason, branchId, practitionerId, wholeDay, startTime, endTime, subtractive }],
  summary: { clinicians, slotsOffered, booked, open } }
```

`status` is named by the **server**. A client deriving "Off" from `slotsOffered === 0` would report a
clinician whose cap is zero, or whose window is shorter than one slot, as being on leave.

`booked` counts everything but a cancellation. A no-show consumed its slot and a completed visit used it;
both are load the clinician carried, and a roster that hid them would report a full afternoon as free.

`open` is floored at zero. A walk-in is booked without consuming a slot, so the difference can go negative,
and "−2 open" is not a number anyone can act on.

### 3.2 Extra clinics with nowhere to come from

An ad-hoc exception carries hours and no slot length — in the generator the length comes from whichever
availability rule the window is generated against. So an extra clinic for somebody with **no** rule at that
clinic generates nothing, and the endpoint reports the line with zero slots rather than inventing a slot
length. That is the true answer: the calendar will be empty, and the roster says so where somebody can see it.

### 3.3 Capacity is not availability, and the two numbers differ on purpose

The booking calendar shows **16** on a Monday the roster calls **18**. Both are right, and they answer
different questions:

| | number | means |
|---|---|---|
| Roster, weekly pattern | **Slots a day** — 18 | what 09:00–15:00 at twenty minutes YIELDS, cap included |
| Roster, today's roster | Offered 18 · Booked 2 · Open 16 | the same capacity, and what is left of it |
| Booking calendar badge | **16** | slots still bookable: materialised, in the future, not held or booked |

Verified against the live data: doctor `129d2a05…` on 24 August has 18 materialised slots and 2 taken, and
on the 25th has 18 and 3 — the calendar showed 16 and 15. Today's badge is smaller again because a slot that
has already passed is not bookable.

The weekly-pattern column is therefore named **Slots a day** rather than "Slots offered": *offered* reads as
"slots you can still book", which is the calendar's number and not this one. The day view keeps "Slots
offered" because Booked and Open sit beside it and settle the question there.

---

## 3.4 A value this service prints is a value it reads

Saving an edited pattern returned **500**, and the body it refused was the one the service had just given
the client.

Every read endpoint in emr formats a `TimeOnly` as `HH:mm` — opening hours are stated in minutes, and a
coordinator's screen shows `09:00–13:00`. .NET's built-in JSON converter requires seconds: it accepts
`"09:00:00"` and throws on `"09:00"`. So the edit form loaded `09:00`, posted `09:00`, and was told the JSON
could not be converted — surfacing as an unhandled 500 rather than a 400 naming the field.

`CreateRosterException.StartTime` and `EndTime` are the same type, so recording a **part-day** absence
("away 11:00 to 13:00") failed identically. That stayed hidden because a whole-day exception sends nulls and
whole-day is the default.

`HourMinuteTimeOnlyConverter` reads `HH:mm` and `HH:mm[:ss[.fffffff]]`, and writes `HH:mm`. Seconds still
work, so nothing that worked stops. Fixing it in the browser — padding `:00` at the one call site that broke
— would have left the asymmetry for the next caller: a service that prints a value in one format and refuses
to read it back in that format has a bug in its contract, not in its callers.

---

## 4. A branch guard that was never armed

Found while writing the endpoint's first test, which returned every clinic's rules to a caller assigned to one.

`BranchScopeResolver.ResolveAsync` returns a `BranchScopeState` carrying two things: a `Context` (which
branches) and a `Mode` (what kind of reach). Four services — **emr, inventory, orders, provider** — copied the
context onto the request-scoped state and dropped the mode:

```csharp
ctx.RequestServices.GetRequiredService<BranchScopeState>().Context = state.Context;   // and not .Mode
```

`BranchScopeState.Mode` defaults to `ScopeMode.MemberScoped`, deliberately, so that it agrees with
`BranchContext.Unrestricted`. And `MemberScoped` is the one value meaning *the branch dimension does not
restrict this caller*. So every consumer reading `branch.Mode` was told the caller was unrestricted, whoever
they were:

* `BranchWriteScope.ResolveTarget` fell to its default arm and returned the branch id **off the request body**,
  never tested against the permitted set;
* `BranchWriteScope.RefuseUnlessWritable` returned `null` for every row;
* `BranchQueryScope.ApplyBranchScope`, at the call sites that used `branch.Mode`, added no predicate at all.

`BranchWriteScope`'s own file describes the hole it was written to close — a set-scoped caller writing to
clinics they do not run — and this reopened it one layer above, for every mode. A branch-scoped coordinator
could read and edit another clinic's weekly pattern.

### 4.1 Why it survived

The endpoints that re-derive the mode from the principal through their own private `BranchModeOf` helper were
unaffected. There were three copies of that helper in emr alone; the shared field written to replace them was
the empty one. So every surface anyone would have thought to test behaved correctly, and only the ones that
had adopted the newer, better-documented seam were open.

Thirteen emr tests then failed on the fix, all of them writes by a caller with no branch assignment — which
the test factory's own comment describes as correct behaviour ("a BranchScoped caller with none is narrowed to
an empty set and sees nothing"). They were passing because the guard was inert. They now name a clinic.

### 4.2 The guard on the guard

`BranchScopeModeCarriedTests` reads every `services/*/Api/Program.cs` that resolves a branch scope and fails
if it assigns the context without the mode — plus a second test asserting the scan finds at least four such
files, because a rule that reads an empty set passes for the wrong reason.

Registered as `INV-THE-RESOLVED-BRANCH-REACH-REACHES-THE-GUARD`, severity Critical.

---

## 4.3 The other half: a supervisor with no way to say which clinic

Two symptoms, reported separately, that are one problem seen from two places.

**"The branch selector is flaky — sometimes it shows, sometimes it doesn't."** `useBranchContext` resolved
`/me/branches` once and gave up on any failure, silently. One 401 while the token was still being exchanged,
one gateway timeout, or identity-service answering a moment late left the caller with **no switcher for the
rest of the session** — and nothing on screen admitting it. It now retries four times with backoff, and
distinguishes a request that FAILED from an answer that was EMPTY: a 200 listing no branches is a real answer
(the fixture harness, a caller with no assignment) and is not retried.

Fail-soft was right for the case it was written for. Fail-soft with **one attempt and no signal** was not.

**"Book Appointment doesn't show a branch selector."** It never did, deliberately — reception books into its
own clinic and the server refuses a request naming another, so a picker could only offer a choice that 403s.
That reasoning holds for a coordinator and breaks for a clinics manager, who has **no active branch until
they filter**: `BranchWriteScope.ResolveTarget` refuses the booking with `branch-target-required`, and the
note on the screen told them to *"switch branches in the header"* — a control that filters rather than
switches, starts cleared, and (per the first symptom) might not be there at all. Booking was unreachable and
the screen's own advice led nowhere.

So the mode now follows the caller's reach, subscribed to the same active-branch store `useAsync` watches:

| caller | mode | note |
|---|---|---|
| reception / coordinator | `fixed` | "booked in your active branch" |
| clinics manager, unfiltered | `choose` | "you run several clinics, so this booking has to name the one it is for" |
| clinics manager, filtered | `fixed` | "booking into the clinic you have filtered to" |

**And the answer to "does this view show only one branch?"** — no. A set-scoped caller with no filter is
served every clinic in reach: `BranchQueryScope` returns the whole permitted set, and the roster's day view
carries a Clinic column precisely for that state. Filtering narrows it; clearing the filter restores all six.

---

## 4.4 A service that was routed and never deployed

The Inventory screen showed *"the service couldn't complete this request"* on both of its reads.

`inventory-service` has been routed from Kong since 25.5. Its schema is migrated, the service builds, its 68
tests pass — and there was **no compose service and no Dockerfile**. So `/api/v1/inventory` resolved to a
hostname that does not exist on the network, every request died at name resolution, and the SPA reported the
wording of a transient fault for an upstream that had never been deployed at all. The screen has never worked
in a real deployment.

**A route to nowhere is worse than a missing route.** A missing one 404s at the edge, which reads as "not
built yet". A route to a host nobody runs looks exactly like an outage of something that exists, so the first
thing anyone does is go looking for the outage.

`check-kong-route-coverage.py` did not catch it because it asks the opposite direction — does every resource
this platform *serves* have a route. Nothing asked whether every host Kong *forwards to* is deployed.
`check-kong-upstreams.py` now does, with a C# twin (`KongUpstreamsAreDeployedTests`) so the rule is nameable
in the registry and runs in the ordinary test suite as well as the lint lane. Verified by removing the
service from compose and watching the gate fail.

### 4.4.1 Three faults, one screen

Deploying the service turned "the service couldn't complete this request" into the same message with a
different cause, twice. Each was invisible to a different guard.

**1. No outbox table.** `AddHbmpDurableOutbox<InventoryDbContext>` stages events into
`inventory.outbox_message`, and nineteen services carry a `9000_outbox.sql` creating it. Inventory shipped
without one. The consequence was not a lost event: `AddHbmpEvents` reroutes the **audit** client through the
same outbox, so an audited READ writes there too — and with no table every request ended in
`42P01: relation … does not exist` and answered 500, while the relay logged the same failure once a second
forever.

`OutboxRelayRegistrationTests` could not see it: it checks registration against registration ("a durable
outbox without a relay is a postbox with no postman"), and inventory had **both**. Nothing checked that the
table either of them talks to exists. It does now, read from the migrations so it fails on a developer's
machine rather than after a deploy.

**2. No `IMemoryCache`.** `HttpBranchDirectory` caches a caller's permitted branch set for ≤60s and takes
`IMemoryCache`; `AddMemoryCache()` was never called. That is not a slower path but a dead one — the typed
client cannot be activated at all, so every request threw at DI time. emr, orders, pharmacy, policy and
reporting all register it beside their own copy of this directory; inventory, the fifth copy, did not.

All sixty-eight of inventory's tests passed throughout, and could not have done otherwise:
`InventoryApiFactory` **replaces `IBranchDirectory` with a fake** so the reach rules can be tested without
admin-service. That is the right substitution for those tests, and it means the real implementation — the one
with the missing dependency — is never constructed anywhere in the suite. It also sets
`Events:UseInMemoryOutbox`, which is why fault 1 was equally invisible.

> **A test factory that removes the real implementation of a seam can never find a wiring fault in it.**

`ProductionWiringTests` boots the app with the same settings and *none* of the substitutions, and asks the
container to construct what the fakes stand in for. It makes no HTTP call and needs no database — the fault
is in the object graph, and that is what it reads. Verified by removing `AddMemoryCache()` and watching it
fail.

**3. And the deployment itself**, above.

---

While there: **Record a movement** is now a button and a dialog. A nine-field form sat permanently open
*between* the stock table and the ledger — so the two things the screen exists to show, a balance and the
movements that produce it, were separated by the form that produces them. Reading the ledger meant scrolling
past a form nobody had asked for; and on a screen where both reads were failing, the form was the only thing
on it that looked healthy. The dialog stays open after a successful post, because a storekeeper booking in a
delivery records several lines in a row.

---

## 5. Both roles, one screen

A branch coordinator and a clinics manager hold **exactly the same permission set** (design 42 §1) and differ
only in how many clinics they reach. Nothing on this screen is gated by role; the differences are all reach:

| | Branch coordinator | Clinics manager |
|---|---|---|
| Clinic filter | absent — the app-bar switcher decides | present, and clearing it means "all of them" |
| Clinician list | their clinic's | every clinic they run, filterable |
| Day roster | their clinic | any clinic they run, one at a time |
| Writes | stamped with their clinic | must name the clinic |

A control that appears because somebody has a choice to make, never because they have more authority.

---

## 6. Invariants added

| id | statement |
|---|---|
| `INV-THE-RESOLVED-BRANCH-REACH-REACHES-THE-GUARD` | a service that resolves a branch scope carries both halves of it — dropping the mode tells every guard the caller is unrestricted |
| `INV-A-DAYS-ROSTER-IS-COMPUTED-WHERE-AVAILABILITY-IS` | the day roster comes out of `SlotGeneration`, never a second derivation |
| `INV-A-ROSTER-NAMES-EACH-CLINICIAN-ONCE` | one row per clinician, clinics named where ambiguous, free days visible |
| `INV-A-VALUE-THIS-SERVICE-PRINTS-IS-A-VALUE-IT-READS` | a time crosses emr's wire as `HH:mm` in both directions |
| `INV-A-WORKING-DAY-CAN-ALWAYS-BE-ADDED-WHERE-THE-CLINIC-RUNS` | a new rule inherits the clinic's service point from any rule at that clinic |
| `INV-A-CALLER-WHO-RUNS-SEVERAL-CLINICS-CAN-NAME-ONE` | the switcher retries a failed resolve; booking asks for the clinic when no filter is set |
| `INV-A-ROUTED-SERVICE-IS-A-RUNNING-SERVICE` | every host the gateway forwards to is a deployed compose service |
| `INV-A-STAGED-OUTBOX-HAS-A-TABLE-TO-STAGE-INTO` | a service registering a durable outbox has a migration creating it |
| `INV-THE-CONTAINER-CAN-BUILD-WHAT-THE-SERVICE-SHIPS-WITH` | the production graph can construct what a test factory substitutes |

---

## 7. Deliberately not done

* **No calendar grid.** A week-by-hour grid looks like the answer and is not: it needs a decision about what a
  cap looks like when it is smaller than the window, and about how two clinics on one day are drawn. The table
  says both in words.
* **Adding a working day at a clinic that has no pattern at all.** A new rule inherits the clinic's provider
  and location — its service point, which this screen cannot ask for — from any rule at that clinic. Where a
  clinic has none, there is nothing to inherit and the pane says so.

  This was narrower and wrong at first: the source was the CLINICIAN's own rules, so Dr Omar Adel — assigned
  to Maadi, with no pattern — could never be given one, even though four colleagues have Maadi patterns
  carrying the same service point. Removing somebody's last day at a clinic took the Add button with it, for
  the same reason. Widening the source to every rule in reach fixes both.
* **Retiring a rule still does not retract the slots it generated.** That is the server's behaviour and the
  right one — the appointments booked into them are real — and the confirmation says so rather than hiding it.
* **No capacity forecast.** "Will next week be short?" is a genuinely different question, and answering it
  from this data would mean projecting exceptions nobody has recorded yet.
