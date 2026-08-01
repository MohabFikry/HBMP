# callcentre-service

The **contact-centre bounded context** (phase 15). A central hotline agent takes a call, **verifies the caller**,
searches the member across **all branches** (MemberScoped — design 37 §3), views a **minimum-necessary, clinical-free
360**, and **books / reschedules / cancels** appointments through the existing emr engine, updates contacts, and
**logs the call**.

## The defining control: verify before you disclose
No member detail is returned until the agent records a **successful caller verification** for **this interaction**
and **this beneficiary**. A verification requires **≥ 2 confirmed identifier TYPES** (configurable), is valid only
for the interaction it was recorded on, and expires on **whichever comes first**: the interaction closing, or
`VerificationService.VerificationTtl` (60 minutes) elapsing since it was recorded. Enforced server-side by
`VerificationService.IsVerifiedAsync` (Infrastructure), which every disclose/act endpoint (15.2–15.4) consults.

> **Why there are two expiries.** Closing used to be the only one, which made the control depend on a later
> request succeeding — and the SPA's close request had been rejected (it did not send the `summary` that 20.3b
> made mandatory) on every call, so in practice no interaction closed and no verification ever expired. The TTL
> is the backstop: not a limit on how long a call may run, but the point past which a recorded verification is
> no longer evidence about who is on the line.

**A type is challengeable only while its value stays undisclosed.** `FullName` is deliberately NOT in
`VerificationPolicy.ChallengeableTypes`: the display name is shown on the pre-verification search hit by design,
so "confirm your name" is answerable off the agent's own screen. `MemberNo` remains challengeable and the
pre-verification projection returns it **masked** (`MemberMatch.MaskedMemberNo`) to keep that true.

**Attempts are capped.** After `VerificationPolicy.MaxFailedAttempts` (3) failures the interaction refuses
further attempts with `429` (audited at Warning) — including one that would have passed. Every failure was
always recorded; none of them used to stop the next.

**Writes to a call record are owner-scoped.** Patch, close and summary-edit require the agent who took the call,
or a supervisor/manager. The policy engine is role + tenant only (MemberScoped, no per-record ABAC), so this is
enforced in the endpoint — see `NotOwner` in `Interactions.cs`.

## The privacy rule for the call log
`caller_verification` stores only **which identifier TYPES** were confirmed (e.g. `["MemberNo","DateOfBirth"]`) —
**never the values** the caller recited. Those values live in patient-service and are not duplicated here.

## No clinical data, ever
The Call Centre never sees diagnoses, results, prescriptions, EMR notes or examination detail — only that an
appointment exists (type, time, branch, doctor name + specialty). Enforced by server-side projection (15.2).

The proof is an **allow-list over every string field** in the `Member360` graph, so a new free-text field fails
the test until someone states what it holds. The older checks — property names, plus a serialized instance the
test populates itself — are kept but are not the guarantee: they cannot see a clinical value in a neutrally
named field, and `MemberFollowUp.Reason` (emr's free-text follow-up reason, verbatim) passed both of them for
as long as it existed. It is now absent from the projection *and* the sibling DTO, so it is never deserialized.

## Layout
- `Domain/` — `CallInteraction`, `CallerVerification`, enums, `VerificationPolicy` (pure rules), `CallRef` key.
- `Infrastructure/` — `CallCentreDbContext` (schema `callcentre`), `VerificationService` (the gate), `CallRefIssuer`,
  `Migrations/0001_callcentre.sql`.
- `Api/` — `Interactions.cs` (15.1), members/search + 360 (15.2), appointment actions (15.3), contacts (15.4).
- `Tests/` — pure verification rules + env-gated (`CALLCENTRE_TEST_DB`) datastore tests (gate, expiry, types-only);
  `CallControlsTests` covers owner-scoped writes, the supervisor override, the attempt cap, and TTL expiry on an
  interaction that is still Open.

## Scopes (authorized at gateway + service)
`callcentre:interaction` (open/patch/close/list), `callcentre:verify`, `callcentre:read` (search + 360),
`callcentre:act` (book/reschedule/cancel + contacts). Role `call_center`; `call_center_supervisor`/`manager` get the
team view. See `libs/authz/CallCentrePolicies.cs`.

## Boundary decision
Kept separate from case-service on purpose — see `docs/adr/0018-callcentre-service-boundary.md`.

## Migrations
`psql -h localhost -p 55432 -U hbmp -d hbmp -f Infrastructure/Migrations/0001_callcentre.sql`
