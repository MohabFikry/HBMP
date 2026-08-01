# callcentre-service

The **contact-centre bounded context** (phase 15). A central hotline agent takes a call, finds the member across
**all branches** (MemberScoped — design 37 §3), works from a **minimum-necessary, clinical-free 360** and the
member's **patient profile**, **books / reschedules / cancels** appointments through the existing emr engine,
updates contacts, and **records one summary of the call**.

## Caller identity is confirmed on the phone

**The agent verifies the caller during the conversation. The platform records that they did — it does not
administer the check.** Opening a member's file writes one attestation
(`caller_verification`, `method = 'OffSystem'`) and **binds the interaction to that beneficiary**.

> **What this replaced, and why it is written down.** Until 2026-08 the platform ran the check itself: the agent
> ticked ≥2 identifier TYPES on screen, and a threshold, a masked member number, a 3-strike lockout and a
> 60-minute TTL all existed to make that on-screen challenge honest. The operation moved the check to the phone,
> so every one of those became the shape of a control rather than a control — a threshold on a set nobody
> submits, a cap on attempts that cannot fail, a mask on a number the agent is now shown in full. They were
> removed rather than left in place looking load-bearing. `docs/superpowers/specs/2026-08-01-call-centre-portal-redesign-design.md`
> records the decision.

### What the gate still enforces

`VerificationService.IsVerifiedAsync` — consulted by every disclose/act endpoint — asks the two questions that
survive the change:

1. **Is this call bound to this member?** A call discloses the member it was opened against and no other. An
   agent cannot open one member's file and read a second member's details through the same call.
2. **Is the call still open?** Closing the interaction ends disclosure. It is now the *only* expiry, so
   `CallControlsTests` pins it directly — and pins the **absence** of a time-based one too, so that a long call
   is understood as a long call rather than a bug someone later "fixes" with a TTL.

Both questions are answered twice (the interaction's binding, and the verification row's own beneficiary) —
deliberately redundant, and the redundancy is verified: removing either alone leaves the suite green, removing
both fails it.

### The record stays honest about the past

`method` distinguishes an `OffSystem` attestation from a historical `OnSystem` challenge, defaulting to
`OnSystem` in both the entity and `0006_verification_method.sql`. Every row written before that column existed
*was* an on-screen challenge; re-labelling them as attestations nobody made would misreport what the platform
did on a real call, and this table is audit evidence rather than a cache.

`VerifiedIdentifierTypes` is **empty** on an off-system row. The agent does not report which identifiers they
asked for, so recording a plausible set would be inventing evidence — and identifier types sent by a client are
ignored rather than stored, so a stale client cannot write one either.

## The privacy rule for the call log
`caller_verification` never stores the identifier VALUES a caller recited. Those live in patient-service and are
not duplicated here. For on-system rows it stores only which TYPES were confirmed.

## One writable text field per call
`Summary` is the operational account of the call, required at close unless the outcome is `Abandoned`, capped at
500 characters, and read by other roles through the patient profile (design 39 §5b). Corrections go through
`PATCH /{id}/summary`, which writes a revision and sets a visible "edited" marker.

There was a second field — `Notes`, the agent's private working text — kept apart so that widening the audience
for call history could not silently widen the audience for whatever was typed mid-call. The call centre now
writes **one** account of the call, which is the one other roles read, so the distinction had nothing left to
protect. The column survives and old notes stay readable; nothing writes to it.

**Writes to a call record are owner-scoped.** Patch, close and summary-edit require the agent who took the call,
or a supervisor/manager. The policy engine is role + tenant only (MemberScoped, no per-record ABAC), so this is
enforced in the endpoint — see `NotOwner` in `Interactions.cs`.

## Search
One query term, matched by eligibility's reception index against **name, phone, card/member number, national ID,
passport, refugee ID and UNHCR number**. Multi-word queries are treated as a name. There is no type picker: the
index has always matched every column at once, so a picker in front of it only ever set the on-screen keypad —
and its own help text said so.

## No clinical data, ever
The Call Centre never sees diagnoses, results, prescriptions, EMR notes or examination detail — only that an
appointment exists (type, time, branch, doctor name + specialty). Enforced by server-side projection (15.2).

The proof is an **allow-list over every string field** in the `Member360` graph, so a new free-text field fails
the test until someone states what it holds. The older checks — property names, plus a serialized instance the
test populates itself — are kept but are not the guarantee: they cannot see a clinical value in a neutrally
named field, and `MemberFollowUp.Reason` (emr's free-text follow-up reason, verbatim) passed both of them for
as long as it existed. It is now absent from the projection *and* the sibling DTO, so it is never deserialized.

The same holds in the **patient profile**: `call_center` resolves to a row in the design-39 §4 matrix giving
identity, coverage, referrals, documents, financial, timeline and full call history — no allergies, no
encounters, no investigations, no prescriptions. Removing the identifier challenge did not widen it.

## Layout
- `Domain/` — `CallInteraction`, `CallerVerification`, enums, `CallRef` key, `CallSummaryRules`.
- `Infrastructure/` — `CallCentreDbContext` (schema `callcentre`), `VerificationService` (the gate), `CallRefIssuer`,
  `Migrations/`.
- `Api/` — `Interactions.cs` (15.1), members/search + 360 (15.2), appointment actions (15.3), contacts (15.4).
- `Tests/` — pure domain rules + env-gated (`CALLCENTRE_TEST_DB`) datastore tests; `CallControlsTests` covers
  owner-scoped writes, the supervisor override, the interaction binding, closing as the one expiry, and the
  historical-method default.

## Scopes (authorized at gateway + service)
`callcentre:interaction` (open/patch/close/list), `callcentre:verify` (record the identity attestation),
`callcentre:read` (search + 360), `callcentre:act` (book/reschedule/cancel + contacts). Role `call_center`;
`call_center_supervisor`/`manager` get the team view. See `libs/authz/CallCentrePolicies.cs`.

## Boundary decision
Kept separate from case-service on purpose — see `docs/adr/0018-callcentre-service-boundary.md`.

## Migrations
Applied by hand, in filename order — see `docs/runbooks/deploy-and-rollback.md`.

```sh
psql -h localhost -p 55432 -U hbmp -d hbmp -v ON_ERROR_STOP=1 \
  -f Infrastructure/Migrations/0001_callcentre.sql
```
