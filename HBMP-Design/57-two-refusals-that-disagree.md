# 57 — Two refusals that disagree

*Phase 19.8. The plan and the contract get the treatment 19.7 gave the payer — a detail, an edit, a status
change with a mandatory reason, a history twin. And the one place the three records deliberately part company:
what happens when you try to switch one off while people are still relying on it.*

---

## 1. The same gap, two levels down

19.7 found the payer could be created and then never corrected, switched off, or explained. The two entities
beneath it had the identical gap, for the identical reason — a create endpoint, a list, and nothing else.

| | `policy.plan` | `policy.policy` |
|---|---|---|
| had | create, list, amend-a-version | create, renew |
| could not | be renamed, recategorised, or withdrawn | be edited at all |
| `status` | accepted `Inactive` since 0005; **nothing ever wrote it** | accepted `Suspended` since 0001; **nothing ever wrote it** |
| actor | `created_by`/`updated_by` since 0005, never a name | **no subject columns at all** |

The policy row is the oldest table in the schema. It has carried `created_at` and `updated_at` since 0001 and
has never recorded *who* — survivable while the only write was the create, because the audit event named the
actor and the row was never touched again. It stops being survivable the moment the row becomes editable,
because "who suspended this contract, and when" is the first question anybody asks of a suspended contract.

So `0021` adds the reason/actor columns to both, gives `policy.policy` its subject columns fifteen migrations
late, and creates `plan_history` and `policy_history` on the trigger pattern `provider.practitioner_history`
(0014) established and `payer_history` (0020) followed.

---

## 2. The part that is not symmetric

Three records, three "switch it off" controls, and **the right answer is not the same for all three.**

### 2.1 The payer refuses

Deactivating a payer that still funds active policies is a `409` with the count (19.7 §3.2). A payer is a
catalogue row; cascading its deactivation would end cover nobody reviewed, from a four-word confirmation
dialog.

### 2.2 The plan refuses too, and for the same reason

Withdrawing a plan whose version is attached to an active policy is refused the same way:

```
409 PLAN_IN_USE
"This plan is still attached to 3 active policies. Detach it there first — withdrawing it from the
 catalogue while members are being enrolled onto it would leave those enrolments resolving against a
 product the catalogue says is gone, and nothing downstream would say so."
```

Worth being precise about what withdrawal is *not*: it does not touch a single plan **version**. Superseded
and Retired versions stay resolvable forever, because a claim for care given last March must still be judged
by March's rules. Withdrawing a plan removes it from the catalogue; it does not rewrite history, and nothing
in `PlanAdminEndpoints` weakens the immutability `PlanEndpoints` enforces.

### 2.3 The contract does **not** refuse — and that is the interesting one

Suspending a policy with 118 active members is allowed. It proceeds. The count comes back in the response.

Because suspending a contract is not a catalogue action — **it is the operation itself.** It is the thing that
happens when a payer stops paying, and it necessarily reaches live members. Refusing it would be refusing the
operation. An administrator suspending a contract already knows people are on it; that is why they are
suspending it.

So the control has the same *shape* — a mandatory reason of at least ten characters, a confirmation, an
audited state change — and the opposite *answer*:

| | payer / plan | contract |
|---|---|---|
| people are relying on it | **refuse**, with the count | **proceed**, with the count |
| the count is | a barrier | context, stated before the button |
| where it appears | the `409` detail | `PolicyStatusResult.activeMembersAffected`, rendered in the dialog |

`PolicyStatusResult` exists solely to carry that number. The screen renders it as an `InlineAlert` above the
reason field — *"118 members are active on this policy right now"* — so the impact is stated **before** the
button rather than discovered after it.

The audit event carries it too (`DecisionReasonCode` ends `;active-members:118`). "Who suspended this, and how
many people did it reach" is one question; splitting it across two stores would make it two.

### 2.4 And one move that has no way back

`Expired` is where a contract ends, not a state it passes through. Resuming one would silently re-open cover
for everybody it ended, so the server refuses:

> An expired policy is not resumed. Renew it — that issues a successor contract linked to this one, which is
> what re-opening cover actually is.

The screen follows: End sits outside the suspend/resume toggle, carries the `bin` glyph rather than the lock,
and its confirmation says *"This cannot be undone — the way back is a renewal"* where suspend says *"It can be
resumed at any time."* A dialog that overstates on the reversible cases is one nobody reads on the
irreversible one.

---

## 3. What each record now holds

### 3.1 The plan

`GET /plans/{id}` answers "which plan" and "what is riding on it" together — versions by status, policies
selling it, members on it, and the **sellable window** derived from the versions.

That last one has a subtlety worth recording. `MAX(effective_to)` is ambiguous: `NULL` means both "no
versions" and "one open-ended version". So the open-ended case is asked separately and wins — an open-ended
plan has no last day rather than an unknown one, and the screen renders "open-ended", not an em dash.

Members are counted through `policy_plan`, not through the policy. A policy can carry several plans, and
"members on this policy" is a different and much larger number than "members on this plan".

The **code** is not editable, for the reason a payer code and a policy number are not: extracts,
reconciliation files and the payer's own systems join on it. The **category** is — it describes the product,
and nothing adjudicated refers to it.

### 3.2 The contract

`GET /policies/{id}` answers with the terms, the window state, and the book of business. Two projections
carry rules from earlier phases:

- **`windowState`** is `NotYetStarted | InForce | Ended`, projected server-side. A policy's own effective
  window is not its status, exactly as a payer's funding agreement is not its status (19.7 §2.2) — an
  **Active** policy whose window closed last month is the combination somebody has to act on, and the screen
  says so in words.
- **`terms`** is withheld as a whole block (`null`) from a caller who may not read contract terms, never as
  four nulls. A block of nulls reads as "not recorded", which is a different and much worse answer about a
  member cap that exists.

`effectiveTo` is **inclusive** here, unlike the payer's exclusive agreement end. That is a real inconsistency
in the schema and it is preserved deliberately: the column has meant *the last covered day* since 0001, and
changing it to match a newer table would silently move every existing policy's last day. The DTO says which
it is; the form labels it "Until (inclusive)".

### 3.3 The cap that cannot be set below the people already under it

`PUT /policies/{id}` refuses a `maxMembers` below the active enrolment count:

> 118 members are already active on this policy, so the cap cannot be set to 50.

Stored, that would put the contract permanently over its own ceiling, and every enrolment check reading the
cap would refuse for a reason nobody can act on — the members are already there.

---

## 4. Two things about authority

### 4.1 The scopes are different, and that is not an oversight

Plan writes require `policy:admin`. Contract writes require `policy:write`.

A payer and a plan are the benefit **product**, and belong to the Policy Administrator. A policy is a
**membership** artefact — Beneficiary Management has issued contracts since 19.2 and already holds
`policy:write` for `POST /policies`. Putting an *edit* of the same row behind a different scope from its
*create* would mean the team that issues a contract cannot correct its dates.

Two mirrors on the web, accordingly: `mayAdministerBenefitProduct` and `mayAdministerMembership`. Both are
mirrors — the server refuses either way — and both decide whether the affordance is **rendered**, never
whether it is enabled.

### 4.2 The payer restriction reaches the records now, not just the register

19.5 restricts a user to a set of payers. `policy-query` narrows the list and refuses a named payer outside
the set. But every route in `PolicyContractEndpoints` addresses **one policy by id**, so the same rule has to
be applied per row or the restriction protects the register and not the records in it.

All four contract routes now check it, audit the denial, and answer `403` rather than `404` — an empty answer
reads as "no such policy". A policy with **no** payer is readable only by an unrestricted caller: a row that
might belong to any payer is not one payer's book of business.

---

## 5. The screens, and one thing extracted

Both follow the payer's shape: a searchable master list, a detail card carrying the record's own facts and its
book of business, an icon row of actions, and modals for create/edit, the status move and the history.

**What is new is that they do not each implement it.** `AdminRecordControls.tsx` holds `RecordActions`,
`ReasonDialog`, `HistoryModal` and `Fact`, and all three screens use them.

That extraction is a direct consequence of design 56. The audit that ran between these two phases counted what
happens when a pattern is repeated rather than shared: eight ad-hoc wrapper classes around one checkbox, four
hand-picked field widths, five different ways of building a filter bar. Three screens each writing their own
confirmation dialog would drift the same way — and this one carries a rule that must not drift, because it is
the difference between a change somebody can explain next year and one they cannot.

Two behaviours live in the shared module and are therefore true on all three screens:

- the confirm is **unpressable** until the reason reads like a sentence;
- the dialog **stays open** when the write is refused, so the caller can render the RFC 7807 detail. That is
  what carries "this plan is still attached to 3 active policies" to the operator instead of dismissing it
  along with the typed reason. (It works because 33.11 taught `ConfirmAction` to swallow a rejection and stay
  up — see design 56 §5.4.)

One more, small: an empty history on an old record now says *why* it is empty. The triggers record from the
migration forward, so a row nobody has touched since has nothing yet — and "No history recorded." on a
five-year-old policy reads as a fault unless the screen says otherwise.

---

## 6. What is now guaranteed

16 backend tests (`PlanAndPolicyAdministrationTests`) and 15 web tests
(`policy-plans-and-contracts.test.tsx`), on top of the 512 the policy service already had.

| claim | where |
|---|---|
| A plan's names, category and description are correctable; its code is not | backend + web |
| A plan needs a name in both languages | backend |
| **Withdrawing a plan an active policy sells is refused, with the count** | backend + web |
| A plan nobody sells withdraws and returns, with its reason | backend |
| A plan status change with no readable reason is refused | backend + web |
| The plan detail counts versions, policies and members, and derives the sellable window | backend + web |
| Plan history is newest-first with the actor named | backend |
| Only a product administrator may write a plan | backend + web |
| A contract's window, cap, payer and notes are renegotiable; its number is not | backend + web |
| A backwards window and a cap of zero are refused | backend |
| A cap below the active enrolment is refused, with the count | backend |
| **Suspending a contract proceeds and reports how many members it reached** | backend + web |
| An expired contract is renewed rather than resumed | backend + web |
| A contract status change with no readable reason is refused | backend |
| Contract history records the suspension and its reason | backend |
| A payer-restricted caller is refused a contract outside their set, on all four routes | backend |
| A caller who may not read contract terms gets no terms block at all | backend + web |

---

## 7. Deliberately not done

1. ~~**The renewal flow has no screen.**~~ **Closed in the same phase.** `POST /policies/{id}/renew` now has
   one, because §2.4 tells an operator that renewal is the way back from an ended contract and leaving them to
   find it was not a defensible place to stop. Two details of the screen are load-bearing:

   - **It is a two-stage modal.** The endpoint does two things at once — creates a policy, and optionally
     moves people onto it — and the second half can partially fail. Members map by plan *label* (ADR-0020),
     and anybody the new policy has no matching plan for is reported by name rather than dropped onto a
     default, because a default would silently change what they are entitled to. A modal that closed on
     success would show "Renewal issued" and throw that report away, so the write is stage one and the report
     is stage two, dismissed deliberately.
   - **Carry-forward defaults OFF.** The successor has no plans at the moment it is created — they are
     attached afterwards — so a carry-forward on a fresh renewal maps nobody and reports everybody. Defaulting
     it on would make the common path produce a wall of "could not be mapped" that is not a fault. Switching
     it on says so.

   Renewal is offered on **every** contract, ended ones included, and is the only action on an ended one.
2. **Plan versions are not in the plan's history.** `plan_history` records the plan row; the version timeline
   is its own thing on the same screen. Merging them into one narrative would be better and is a projection,
   not a column.
3. **Suspension does not reach the members.** A suspended policy is a suspended policy; the enrolments under
   it keep their own status. Whether eligibility honours the contract's status is `eligibility-service`'s
   question and is not changed here.
4. **`Sponsor` is still there.** The free-text field `payer_id` replaced in 19.2, kept so pre-19.2 rows stay
   readable. Still outstanding, still not this phase's to retire.
5. **No effective-dating on the contract's own terms.** The cap and the window are mutable columns with a
   history twin behind them. If a claim ever has to be judged against "the cap as it stood on the service
   date", they become versioned rows — the same shape `plan_version` already has, and for the same reason.
   Nothing adjudicates against them today.

---

## 8. Addendum — the migrate step that was skipped

*Found while closing §7.1, in a different service, and it belongs here because it is the same class of fault
this phase spent its whole length avoiding.*

The Providers Directory answered **500 on every load**, with nothing on screen but *"The service couldn't
complete this request."* The cause:

```
System.InvalidOperationException: Cannot convert string value 'Radiology'
  from the database to any value in the mapped 'ProviderType' enum.
```

Design 45 §1 runs the `Imaging` → `Radiology` rename as **expand → migrate → contract**. Migration `0011`
expanded both CHECKs to accept either spelling. `0012` backfilled every existing row to the new one. The
deferred `0013` will narrow the CHECKs once nothing writes the old spelling. All three are written, careful,
and heavily commented.

**The migrate step is the code, and it was never done.** `ProviderType` had no `Radiology` member, so from the
moment `0012` ran EF could not materialise a single provider row. Every read of `provider.provider` — the
directory, contracts, locations, the routing lookups — answered 500 together. `ServiceType` had the identical
hole and had simply not been hit yet: no contract line in the data carried the new spelling, and the first
radiology one would have taken pricing down the same way.

Three things worth keeping:

1. **A backfill without its enum is a schema migration that deletes a table.** Not literally — but every read
   fails, which is operationally the same thing, and it fails *later*, when the deploy looks green.
2. **The read shape saved the SPA and the write shape did not.** `zProviderSummary.providerType` is
   `z.string()`, so the browser would have rendered whatever arrived. `zCreateProviderInput.providerType` is
   an enum, and it was narrow — so even with the server fixed, the Network Team could not *onboard* a
   radiology centre. The one type the database had been storing since `0012` was the one type nobody could
   pick. `zInvestigationOrderType` had been widened correctly, with the reasoning written out; the provider
   contract beside it had not.
3. **The guard reads the migration, not a list.** `EnumsMatchTheDatabaseTests` parses the last CHECK written
   for each column and asserts the enum is a superset. A test that spelled the expected members out by hand
   would be a third copy of a vocabulary whose two existing copies had just disagreed. Only the enum must be
   the superset — a column may legitimately be narrower during the contract phase, which is exactly what
   `0013` will make it.
