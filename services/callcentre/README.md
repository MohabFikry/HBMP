# callcentre-service

The **contact-centre bounded context** (phase 15). A central hotline agent takes a call, **verifies the caller**,
searches the member across **all branches** (MemberScoped — design 37 §3), views a **minimum-necessary, clinical-free
360**, and **books / reschedules / cancels** appointments through the existing emr engine, updates contacts, and
**logs the call**.

## The defining control: verify before you disclose
No member detail is returned until the agent records a **successful caller verification** for **this interaction**
and **this beneficiary**. A verification requires **≥ 2 confirmed identifier TYPES** (configurable), is valid only
for the interaction it was recorded on, and **expires when the interaction closes**. Enforced server-side by
`VerificationService.IsVerifiedAsync` (Infrastructure), which every disclose/act endpoint (15.2–15.4) consults.

## The privacy rule for the call log
`caller_verification` stores only **which identifier TYPES** were confirmed (e.g. `["MemberNo","DateOfBirth"]`) —
**never the values** the caller recited. Those values live in patient-service and are not duplicated here.

## No clinical data, ever
The Call Centre never sees diagnoses, results, prescriptions, EMR notes or examination detail — only that an
appointment exists (type, time, branch, doctor name + specialty). Enforced by server-side projection (15.2) and
proven by an authorization test over the serialized payload.

## Layout
- `Domain/` — `CallInteraction`, `CallerVerification`, enums, `VerificationPolicy` (pure rules), `CallRef` key.
- `Infrastructure/` — `CallCentreDbContext` (schema `callcentre`), `VerificationService` (the gate), `CallRefIssuer`,
  `Migrations/0001_callcentre.sql`.
- `Api/` — `Interactions.cs` (15.1), members/search + 360 (15.2), appointment actions (15.3), contacts (15.4).
- `Tests/` — pure verification rules + env-gated (`CALLCENTRE_TEST_DB`) datastore tests (gate, expiry, types-only).

## Scopes (authorized at gateway + service)
`callcentre:interaction` (open/patch/close/list), `callcentre:verify`, `callcentre:read` (search + 360),
`callcentre:act` (book/reschedule/cancel + contacts). Role `call_center`; `call_center_supervisor`/`manager` get the
team view. See `libs/authz/CallCentrePolicies.cs`.

## Boundary decision
Kept separate from case-service on purpose — see `docs/adr/0018-callcentre-service-boundary.md`.

## Migrations
`psql -h localhost -p 55432 -U hbmp -d hbmp -f Infrastructure/Migrations/0001_callcentre.sql`
