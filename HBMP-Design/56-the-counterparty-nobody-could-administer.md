# 56 — The counterparty nobody could administer

*Phase 19.7. Payer administration in the policy portal: what a payer record has to hold, the three writes it
never had, the refusal that stops a four-word dialog from ending thousands of people's cover, and the payer
restriction that the query surface honoured and the payer list did not.*

---

## 1. What was there

`policy.payer` has existed since migration 0005. It held six things:

| column | what it is |
|---|---|
| `payer_code` | the internal handle |
| `name_en` / `name_ar` | what to call it |
| `payer_type` | `SelfFunded \| Donor \| Government \| PartnerNGO \| Insurer` |
| `contact` | `jsonb NOT NULL DEFAULT '{}'` |
| `status` | `Active \| Inactive` |

The API over it was three routes: `POST /payers`, `GET /payers`, `GET /payers/{id}`. The screen over that was
a four-column read-only table.

A payer is the counterparty a policy is funded **by**. `policy.policy.payer_id` points at it, the utilization
surface rolls up to it (19.4), `admin.payer_assignment` restricts a user to it (19.5), and reporting-service
keeps a dimension-label table fed from `PayerCreated`. It is the top of the commercial hierarchy the entire
benefit book hangs from — and it was the one entity in the product that could be created and then never
touched again.

Concretely, this is what could not be done:

- **A name could not be corrected.** A typo in a donor's Arabic name was permanent for the life of the record,
  and it reached every dashboard through the event feed.
- **A payer could not be switched off.** `status` existed, took `Inactive`, and no code path ever wrote it.
- **An agreement could not be recorded at all.** Whether a grant was still running lived in somebody's inbox.
- **A funding ceiling did not exist**, so "how much have we committed against this grant" was a spreadsheet
  question with no platform answer.
- **Settlement terms did not exist.** Net-30, quarterly invoicing, a 90-day claim submission window — all of
  it in the signed PDF, none of it anywhere a claims officer could read it.
- **`contact` was `{}` on every row ever created.** A column reserved in 0005 for a need nobody then
  implemented, and never given a shape. Nothing read it; nothing wrote it.
- **Nothing could be asked about the past.** No history, no reason, no actor.

### 1.1 The tell

The `contact` column is worth pausing on. It is not that the field was unused — it is that it was *reserved*.
Somebody in 0005 knew a payer needs people attached to it, wrote the column, and left the shape for later.
Four phases later "later" had not arrived, and the column had acquired the one property that makes a JSON blob
dangerous: no owner. The first two call sites to write it would have written two different shapes.

19.7 gives it a type (`PayerContacts`) and exactly one codec (`PayerContactCodec`), which is the cheapest
moment this was ever going to be possible.

---

## 2. What a payer record actually needs

Grounded in the TPA operating model (`health-insurance-tpa-operations`) and in what an NGO benefit
administrator does day to day (`ngo-healthcare-operations`), a payer has **four kinds of fact**, and they are
different kinds:

### 2.1 Identity — who this is

`payer_code`, both names, `payer_type`, plus **`external_ref`**: the reference *the payer* knows this
arrangement by. A donor's grant number, an insurer's licence. Reconciliation is done against **their**
reference, not ours, and a platform that only stores its own identifier makes every reconciliation a manual
lookup.

### 2.2 Agreement — whether this funding is running

`agreement_no`, `agreement_from`, `agreement_to`. Half-open `[from, to)`, matching every other window in the
schema.

**The agreement window is not the status.** This is the most important modelling decision in the phase.
A grant that ran its course and a payer somebody switched off are different facts, and collapsing them loses
the difference between "the funding ended" and "we stopped working with them". So both are kept, and
`Payer.AgreementState(on)` projects one of four answers:

| state | means |
|---|---|
| `Unrecorded` | nobody wrote the window down — **its own answer**, not a synonym for in-force |
| `NotYetStarted` | signed, starts later |
| `InForce` | running today |
| `Expired` | the window has closed |

The screen renders an **Active payer with an Expired agreement** as exactly that, in words, with a warning:
*"This payer is active and its funding agreement has expired."* That combination is the one somebody has to
act on. Hiding it — by deriving status from the window, or by suppressing one chip when the other disagrees —
would remove the only signal that a renewal is overdue.

`Unrecorded` being distinct from `InForce` matters for the same reason: a payer nobody ever wrote a window for
is a gap in the record, and reporting it as "in force" would be a guess presented as a fact.

### 2.3 Financial terms — how much, and on what terms

| column | why |
|---|---|
| `funding_ceiling` `numeric(14,2)` | what the payer has committed. Same numeric shape as `coverage_limit.limit_value`, because the two are compared against each other and two different shapes is how a rounding difference becomes a reconciliation dispute |
| `currency` `char(3)` | not every donor funds in pounds |
| `settlement_terms_days` | "net 30" as the number every signed contract states it as, not as free text a report would sort alphabetically |
| `invoicing_cadence` | `OnClaim \| Monthly \| Quarterly \| SemiAnnual \| Annual` — *how often*, which is independent of *by when* |
| `claim_submission_window_days` | how long after the service date a claim may still reach this payer. **Past it the money is gone whether or not the care was covered**, which is why it belongs on the payer and not in a finance spreadsheet |

A ceiling of **zero is refused**, at the database (`ck_payer_funding_ceiling_positive`) and at the endpoint with
an explanation. Zero is not "uncapped" — it is "funded for nothing", and a payer funded for nothing would
refuse every claim for a reason no screen could explain. Uncapped is `NULL`.

### 2.4 People — who to call

`contact` gains the shape it never had: **three named roles**, not a list.

- `primary` — the day-to-day counterpart
- `finance` — who settles invoices
- `escalation` — who to go to when a claim stalls

Three roles rather than a flat list because the three questions asked of a payer are asked of **different
people**, and a list makes an operator guess which contact is which at exactly the moment they most need to be
right. An entry with every field blank normalises to `null`, so "no finance contact" reads as absent rather
than as a card with a heading and four empty rows.

These are the payer's own staff. **Never beneficiary detail** — this is operational contact data and nothing
else, and the DTOs carry no field that could hold anything otherwise.

---

## 3. The three writes it never had

### 3.1 Update — and the field that is deliberately absent

`PUT /payers/{id}` carries names, type, terms, contacts and notes. It does **not** carry `payer_code`, and that
is the contract rather than an oversight:

> The code is the key extracts, reconciliation files and the payer's own systems join on. Renaming it silently
> re-points every one of those at a payer they will no longer find, and the failure surfaces as a
> reconciliation gap weeks later.

This follows the network-tier precedent exactly (19.1b: "the code and the out-of-network flag can never be
changed"). A code is not correctable — it is *replaceable*, and replacing it means creating the right payer and
moving its policies deliberately. The edit form shows the code as read-only with that sentence beneath it, so
the rule is explained where somebody would otherwise try to break it.

The **type** is editable, unlike the tier's out-of-network flag, because reclassifying a payer that was entered
as `Donor` and is really a `PartnerNGO` is a correction of a description, not a rewrite of anything already
adjudicated. It rides the audit event and the history twin.

**A terms block that is absent CLEARS the terms.** A partial write that silently keeps old values is how a
payer ends up with last year's ceiling and this year's window and no screen able to say which was intended.

### 3.2 Deactivate — and the refusal that is the point of the phase

`POST /payers/{id}/deactivate` and `/reactivate`, both taking a **mandatory reason of at least ten
characters**. Ten, matching the platform's other mandatory reasons — not pedantry: a one-word reason ("old") is
indistinguishable from no reason at all to whoever reads the record next year, and being readable then is the
entire purpose of requiring one.

Then the refusal:

```
409 PAYER_HAS_ACTIVE_POLICIES
"This payer still funds 3 active policies. End or transfer them first — deactivating the payer
 would leave them resolving against a counterparty the platform has been told is finished, and
 nothing downstream would say so."
```

This is the one place in the feature where the convenient choice would have been dangerous. Deactivation could
have cascaded — suspended the policies, or simply not cared. Either would have made a four-word confirmation
dialog end thousands of people's cover, with no preview and no undo. So the write is **refused with the
count**, and the administrator is sent to do the thing they actually meant.

The refusal itself is audited (`deactivation-refused`, with the count as its reason code). A refusal somebody
hit and worked around is worth knowing about.

Deactivating is otherwise **reversible and non-destructive**: it stops the payer being offered as a funder for
new policies. Nothing already enrolled changes, and the record stays readable forever. The dialog says so,
because a dialog that claims irreversibility where none exists is one nobody reads on the writes that really
are irreversible.

### 3.3 History — the twin, not the chain

`GET /payers/{id}/history`, reading `policy.payer_history`: an `AFTER INSERT OR UPDATE` trigger snapshotting
`to_jsonb(NEW)`. Same construction as `provider.practitioner_history` (0014) and
`emr.roster_exception_history` (0016), and for the same reason.

Every write here is *already* audited into the hash-chained trail. That trail sits behind `audit:read` —
Security, Compliance, the DPO — correctly, because it is tamper-evident evidence whose own reads are audited.
But it left the policy administrator who maintains a funding ceiling with no way to ask **who last raised it**,
about a record they own. The information existed, in a store they are rightly not given.

Both stores are written on every change. They answer different questions for different people:

| | audit chain | history twin |
|---|---|---|
| reader | Security / Compliance / DPO | whoever administers the payer |
| gate | `audit:read` | `policy:admin` + payer scope |
| property | hash-linked, tamper-evident | a readable snapshot per change |
| question | "prove what happened" | "who changed this, and what did it say before" |

The actor is snapshotted **by name** as well as by subject, following 0014: resolving names at read time
renders "unknown" for everyone who has since left, and making policy-service call the issuer to draw a history
row is a dependency in the wrong direction for a read that must not fail.

---

## 4. Two things that were wrong before this phase and are fixed by it

### 4.1 The payer restriction the payer list did not apply

19.5 introduced `admin.payer_assignment` and `IPayerDirectory`: a user can be **restricted to a set of
payers**. `policy-query` and `member-query` honour it — the restriction is a predicate inside the SQL,
including the row count, and naming a payer outside the set returns 403 rather than an empty page.

`GET /payers` did not honour it at all.

So a user restricted to one donor could list every counterparty on the platform. Before this phase that leaked
the set of names. **After** this phase, without the fix, it would have leaked every funding ceiling and every
settlement term as well — the feature would have turned a name disclosure into a commercial one.

The list now narrows, and `GET /payers/{id}`, `PUT`, deactivate/reactivate and history each refuse an
out-of-set payer with `403 urn:hbmp:payer-scope-denied` and an audited `PayerScopeDenied` grant event. 403 and
not 404, for the reason `QueryEndpoints` already documents: an empty result reads as "no such payer", which is
a different and misleading answer.

Note the direction this fails in. Branch scope fails closed by returning an empty *permitted* set, which
denies. Payer scope's empty set means *unrestricted*, so an error there would fail **open** —
`HttpPayerDirectory` returns `DenyAll` on a directory outage for exactly this reason, and the payer surface
inherits it.

### 4.2 Commercial terms are withheld as a block, not as five nulls

`AdministrativeProjection.MayReadContract` is the existing rule for who may see contract terms — the policy and
member query surfaces already apply it to `payerId` and `maxMembers`. The payer surface applies it to the whole
financial block:

```csharp
mayReadContract ? new PayerFinancialTermsView(...) : null
```

**Not** to each field. A block of nulls renders as "not recorded", which is a different and much worse answer
to give somebody about a ceiling that exists. `terms == null` means *withheld*, and the screen says so:
*"Funding and settlement terms are restricted for your role. They are recorded — you are not being shown
them."*

The same rule applies to the history: a reader who may not see today's ceiling must not be able to read
yesterday's.

The book of business splits the same way. Counts always; amounts under `MayReadAmounts`; and the
**percentage survives either** — "this grant is 82% committed" is an operational fact, the pounds behind it are
a commercial one. That is the policy-query surface's own rule, applied here. In the same spirit, `null` and
`0` are different: `null` is "you may not see this", `0` is zero, and rendering both as an em dash would tell a
role with no amount access that a payer with a full book of business has none.

---

## 5. The screen

`PolicyPayerAdmin.tsx`, its own lazy chunk. Bundling it with the plan-version editor would have made the
heaviest screen in the portal the price of opening the lightest.

### 5.1 Master list, then one payer in full

The list answers **which payer** and nothing else: code, name, type, agreement state, record status. Search
covers code, both names, the agreement number and the external reference — a search that only matches the name
fails on the one identifier the operator has in hand. Three filter groups (status, type, agreement), sortable
columns, pagination: the house `useTableQuery` + `DataTableView` pattern, so a payer list behaves like every
other list in the product.

Everything else is in the detail, because a payer has four kinds of fact and a sixteen-column table is a table
nobody reads. Selecting a row is the only navigation.

### 5.2 The detail

One header card (name, code, both status chips, the type, and the icon row of actions), then Agreement &
funding, then Contacts, then Notes, with the book of business as a `KpiList` in the header card.

Money renders in the **payer's own currency**. `useFormat().money` is fixed to EGP, and a USD grant shown in
pounds is not a formatting slip — it is a number somebody would act on.

Every status is four cues — hue, icon, shape, text — from `StatusChip`, so the agreement state survives
grayscale and colour-blindness.

### 5.3 Absent, not disabled

`policy:admin` is held by `policy_admin`, `org_admin`, `super_admin`. A claims officer, a finance officer and
the network team hold `policy:read` and reach this screen legitimately — they adjudicate against these terms.
They are shown **no New / Edit / Deactivate control at all**, rather than four buttons that answer 403.

A disabled button teaches an operator that the screen is broken. An absent one teaches them whose job it is.
`mayAdministerBenefitProduct` mirrors the server rule and is a mirror only — the server refuses either way.

The **history** stays available to a reader. It carries the same withholding rule as the payer itself, and
"who changed this" is a question a claims officer disputing a term has every reason to ask.

The edit form carries one guard for a case that should not be reachable. Every role holding `policy:admin`
is also a contract reader, so a form that cannot show the terms should never open — but the server reads an
**absent** terms block as *clear them*, and a save that silently wiped a funding ceiling because the person
saving was not allowed to see it is the worst thing this screen could do. So the save is refused, loudly, if
the terms were withheld. Unreachable today; cheap; and the failure it prevents is silent data loss.

### 5.4 A confirmation that stays open when the write is refused

`ConfirmAction` closed its dialog once `onConfirm` resolved. Its own doc comment already explained why a
callback that *validates and returns early* would be reporting a failure as a success — and a callback whose
write is **rejected** was in exactly that position, except worse: the rejection escaped into the
unhandled-rejection channel, where it was logged as a crash and shown to nobody.

So `ConfirmAction` now keeps the dialog open on a rejected confirm and swallows the rejection, and the caller
renders why — because the caller holds the RFC 7807 detail ("this payer still funds 3 active policies") and the
component holds nothing but a label.

This is the fix that makes §3.2's refusal *reach* anybody. Without it, the count would have been computed,
audited, returned — and dismissed along with the dialog and the typed reason.

---

## 6. What is now guaranteed

Twelve backend tests (`PayerAdministrationTests`) and sixteen web tests (`policy-payers.test.tsx`):

| claim | where |
|---|---|
| A payer carries its agreement, its terms and its people; a blank contact comes back absent | backend |
| An expired agreement reads as `Expired` while the payer stays `Active` | backend + web |
| A ceiling of zero is refused with the reason | backend + web |
| An update corrects names, type and terms and never the code | backend + web |
| A member administrator may not write a payer at all | backend |
| **Deactivation is refused while the payer funds an active policy, with the count** | backend + web |
| A status change with no readable reason is refused | backend + web |
| Reactivating an active payer is a conflict, not a silent no-op | backend |
| History records every change newest-first, with the actor and the ceiling that was set | backend |
| A caller who may not read contract terms gets no terms block **at all** | backend + web |
| A payer-restricted caller sees only their own and is refused the rest (403, on all four routes) | backend |
| The detail counts what actually hangs off this payer, scoped to it | backend |
| A withheld committed total is distinguishable from a zero one | web |
| Money renders in the payer's own currency | web |
| The server's refusal reaches the operator instead of dismissing the dialog | web |

---

## 7. Deliberately not done

1. **The signed agreement itself.** `policy_document` already stores files in MinIO, scoped to `Policy` and
   `Member`. Extending that scope to `Payer` would let the PDF live beside the terms extracted from it. This is
   the largest and most obviously next thing.
2. **Notes and a timeline on a payer.** `NoteScope` is `Policy | Member`; the 19.3c timeline projection
   likewise. A payer would be a third scope in both.
3. **Effective-dated terms.** The ceiling and the settlement terms are *mutable columns* with a history twin
   behind them. If claims ever need to price against "the ceiling as it stood on the service date", these have
   to become versioned rows with a resolver — the same shape `plan_version` already has, and for the same
   reason. Today nothing adjudicates against them, so versioning would be structure with no reader.
4. **Who is scoped to this payer.** `admin.payer_assignment` is owned by admin-service; listing the users
   restricted to a payer on its detail is a cross-service read that does not exist yet.
5. **A deep link from the payer to its utilization.** `scopeUtilization("payers", id)` has existed since 19.4,
   but the standalone Utilization screen only offers a policy picker. Widening it is a change to a different
   screen and belongs to whoever owns that one.
6. **Ceiling alerts.** The detail warns when committed cover exceeds the ceiling. Nobody is *notified*; the
   notification service is not wired to anything in policy administration.
7. **`Sponsor` retirement.** `policy.policy.sponsor` is the free-text field `payer_id` replaced in 19.2, kept
   only so pre-19.2 rows stay readable. The backfill that retires it is still outstanding and is not this
   phase's to do.
