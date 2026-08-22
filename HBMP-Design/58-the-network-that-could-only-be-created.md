# 58 — The network that could only be created

*Phase 19.9. The provider network gets the treatment 19.7 gave the payer and 19.8 gave the plan and the
contract. It needed it more than either: phase 2b built the provider domain as a creation pipeline and never
gave it a second verb, so a primary location could not be moved, a tariff could not be corrected, a staff
account could not be revoked without suspending the whole hospital — and a termination somebody opened by
mistake could only be closed by carrying it out.*

---

## 1. Five sections, one write

The Network portal has five sections. Between them they offered a single mutation: create a provider.

| Section | Offered | Did not offer |
|---|---|---|
| Providers Directory | a searchable list | opening a provider |
| Onboarding | a three-field create ending at `Draft` | every state after `Draft` |
| Contracts & Coverage | a read-only table | raising, correcting, pricing, activating or ending a contract |
| Locations & Users | a read-only table | adding, correcting, moving or closing a location; users were not shown at all |
| Performance | four numbers | — |

The endpoints were not all missing. `activate`, `suspend`, `terminate`, `POST /locations`, `POST /contracts`,
`POST /service-lines`, `POST /credentials` and `POST /users` had existed since phase 2b, most of them with
careful guards and audit events, and **not one had a button.** A provider went live because somebody ran
`curl`. That is the shape of the finding: not an unbuilt backend, an unreachable one.

### 1.1 The type field nobody could type

Onboarding's provider **type** was a free-text `InputField` validated client-side against a hardcoded array
and server-side against an enum. Typing `hospital` produced `Code, legal name, and a valid type are required.`
— with no list anywhere on the screen of what a valid type was. It is a `ComboboxField` now, built from the
same list as the edit form.

---

## 2. What could not be changed, and what that cost

### 2.1 The primary location could never be moved

Exactly one primary location per provider is a partial-unique index, enforced since migration `0001`. The only
location write was `POST /providers/{id}/locations`, so:

- adding a second primary answered `409`,
- there was no demote,
- there was no edit,
- there was no deactivate.

A provider whose head office moved could not be corrected at all. This is not cosmetic: the primary location
is one of the four conditions activation is gated on, and it is the address referrals are sent to.

Promotion is now one transaction that **demotes first**. The order is load-bearing twice over: the reverse
violates the index, and two separate commits can leave the provider with no primary at all — which silently
fails its own activation check while the directory goes on saying `Active`.

### 2.2 A termination request could never be withdrawn

The 2026-08-09 audit replaced provider termination's fake dual control (a `secondApproverSubject` **string**
the terminator typed themselves) with a real one: first POST opens a request, a POST from a different
authenticated subject approves it. Migration `0013` gave the table a `withdrawn_at` column and a `Withdrawn`
status.

**Nothing has ever written either.** The only exit from a request opened in error was for a second person to
approve it — which terminates the provider, revokes every account they hold, and publishes both facts
platform-wide. A control with no cancel is not a control; it is a trap with a confirmation dialog.

`POST /providers/{id}/terminate/withdraw` closes it. Deliberately **not** restricted to the requester: dual
control exists so no one person can terminate a provider, not to stop a colleague closing a request that
should not have been opened. Withdrawing is the safe direction, and both subjects are recorded.

### 2.3 One account could not be revoked

Taking an account away from a provider meant `POST /providers/{id}/suspend`, which revokes **every** account
they hold and stops routing to them. That is an outsized answer to "this person left". `POST
/providers/{id}/users/{userId}/revoke` is the proportionate one, in the same transaction as its event.

### 2.4 The reason bar lived only in the browser

Suspending a provider revokes every account they hold and stops routing to them. Terminating one is
dual-controlled and irreversible. Both have required a reason since phase 2b — and both checked
`string.IsNullOrWhiteSpace` and nothing else.

So `"old"` was an acceptable justification for either. The policy portal has held a ten-character bar since
19.7, the SPA's shared `ReasonDialog` enforces it, and the client asked for a sentence while the service took
a word. A bar that lives only in the browser is not a bar; it is a suggestion to anybody holding a token.

All three provider status endpoints now hold the same bar as the new ones. Activation keeps its **optional**
body — the callers that predate 19.9 send none — but a reason that *is* given has to clear it.

---

## 3. `provider_history` has had no row-level security since 0001

`0001` created `provider.provider_history` with an `AFTER INSERT OR UPDATE` trigger snapshotting
`to_jsonb(NEW)`. `0003` then put RLS on `provider`, `provider_location`, `provider_contract`,
`provider_credential`, `provider_user` and `contract_service_line` — and **not** on the history table, which
has neither a `tenant_id` to filter on nor a policy that could use one.

Every provider row this platform has ever written, for every tenant, sits in one unfiltered table.

Nothing has leaked, for the only reason that matters: **nothing has ever read it.** No endpoint, no query, no
report. The table has been write-only for its entire life, which is also how the gap survived three security
passes — there was no reader to review.

19.9 adds the reader. So the gap closes in the same migration that opens it: `tenant_id` backfilled from the
snapshot itself (`row_snapshot ->> 'tenant_id'`, available because the source column has been `NOT NULL` since
`0001`), then `NOT NULL`, a blank-tenant `CHECK`, and the fail-closed policy `0014` established.

The `SET NOT NULL` carries a `-- migrate-compat: contract-ok` acknowledgement, and the reason it is safe is
specific rather than general: the **only** writer to that table is the trigger, the trigger lives in the
database, and it is replaced in the same migration. There is no deployed application version that inserts
there, so no running writer can be caught out.

`provider_location` and `provider_contract` get twins of their own. A provider-level snapshot cannot record
that somebody moved the primary location to a different governorate or shortened a contract's window, because
neither touches the provider row — and those are the two edits with consequences a month later.

---

## 4. The readiness checklist

`OnboardingWorkflow.GuardActivation` evaluates four conditions and returns the **first** that fails, as a
sentence, in a `422`:

```
Cannot activate: no active contract.
```

So an operator fixing a provider learned about one missing thing per attempt, and only by attempting. Four
round trips to discover four facts the server knew all along, and each one framed as a failure.

`GET /providers/{id}/administration` returns all four plus the guard's own verdict. The screen renders the
checklist before anything is attempted. The guard is unchanged and still the authority: the endpoint *calls*
it rather than restating what it checks, and activation still asks. This is not a substitute for the guard —
it is the operator not having to discover the guard by tripping it.

---

## 5. What is refused, and what is merely reported

57 named this asymmetry; 19.9 has one of each, and the reasoning is the same both times.

### 5.1 Closing the primary location is **refused**

```
409 LOCATION_IS_PRIMARY
"This is the provider's primary location. Make another location primary first — a provider with no
 primary location fails its own activation check."
```

A provider left without one keeps answering `Active` in the directory while quietly failing its own gate, and
the next person to notice is whoever tries to reactivate it in six months.

### 5.2 Terminating the last contract in force is **not**

Ending a contract *is* the operation, not a side effect of one. Refusing it would leave an operator whose
counterparty has walked away with no way to record that.

But the consequence is stated. Terminating a provider's only in-effect contract leaves them `Active` in the
directory and **routable for nothing** — `CapabilityDerivation` returns an empty list, so they stay visible,
selectable, and reachable by no order or referral. The response says so, recomputed after the write from the
same rows the router reads:

```json
{ "status": "Terminated", "providerBecomesUnroutable": true, "providerStatus": "Active" }
```

and the confirmation dialog says it **before** the act, because the consequence is the reason somebody might
not do it.

### 5.3 A tariff in force is superseded, not edited

Service lines split three ways, and the split is about what can retroactively change money:

| | Draft | Active |
|---|---|---|
| add a code | yes | **yes** |
| change a price | yes | no |
| remove a line | yes | no |

Adding a code to a live contract is additive: a service that was not on the list cannot have been priced under
it, so nothing already adjudicated can move. Repricing one changes what a claim submitted yesterday and
adjudicated tomorrow is worth, with nothing recording that the number moved. Same for the contract itself —
once Active, its number and start date are what claims were settled against, and end-dating it into the past
is refused in favour of terminating it, which ends it from today and says why.

---

## 6. The scope that draws the line through this portal

Two roles share the Network portal (design 52 §5): Mersal's **Network Team**, tenant-wide; and
`provider_admin`, a contracted provider's **own** administrator, ABAC- and RLS-bound to their own row.

Both hold `provider:write`. Both, therefore, could have reached every endpoint in this phase — and RLS would
have permitted every one of them, because the rows *are* theirs. A hospital could have edited the dates of its
own contract with Mersal, repriced its own tariff lines, and decided that its own licence was `Valid`.

19.1b had already split the scope that fixes this, for the same reason one level up: `provider:admin` exists
because moving a provider between network tiers is a commercial act sitting in the same scope as editing an
address. `provider_admin` does not hold it (identity `0007`).

So the administration endpoints divide:

- **`provider:write`** — provider identity, locations, staff accounts. A provider correcting their own address
  is their job.
- **`provider:admin`** — contract dates, contract termination, repricing and removing tariff lines, the
  credentialing decision, withdrawing a termination request.

`mayAdministerTheNetwork` mirrors the line in the SPA, so a provider's own administrator sees the record and
**no** control rather than four buttons that answer 403.

---

## 7. Deferred, and named so it is not mistaken for done

1. **`POST /providers` is still behind `provider:write`.** A provider-scoped caller inserting a new provider
   is stopped by RLS (the policy's `USING` clause doubles as its `WITH CHECK`), but it fails as a `500`
   rather than a `403`. The portal hides the control from anyone who is not the Network Team; the endpoint
   should refuse it properly.
2. **Credentials carry no document.** `document_id` is a bare uuid typed into a field. The credential is
   refused as `Valid` without one, which is the rule that matters, but the actual scan lives in
   document-service and this screen should upload to it rather than ask for an id.
3. **Contract history does not include its service lines.** The twin snapshots `provider_contract`; a
   repricing shows on the line, not on the contract's timeline. Draft-only repricing keeps the blast radius
   small, but they should merge.
4. **Suspension does not reach appointments.** Suspending a provider stops routing and revokes accounts.
   Appointments already booked at their locations are untouched and nothing tells the desk. Same shape as
   57 §7's note about enrolments.
5. **`provider_location` deactivation does not check bookings** the way the primary-location rule checks
   activation. Closing a non-primary site with appointments on the books is allowed and silent.
6. **The two roles still share one portal.** Design 07 FR-IAM-003 lists them separately and design 11 §3.3
   gives them different rows. Every "whose view is this" branch in this portal — Performance, Onboarding, and
   now every write control — is a workaround for a split that has not happened. Still design 52 §5.
7. **Nothing here is verified at the pixel level.** No browser will start on this machine; the UI is
   source- and stylesheet-verified only, and the checks that hold the line are the house guards
   (`css-classes-exist`, `display-truth`, `queue-table-view`, `button-icon-policy`, `popup-not-restyled`).
