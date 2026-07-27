# ADR-0024 — Payer scope is a restriction on a user, resolved per request, failing closed to "nothing"

- **Status:** Accepted
- **Date:** 2026-07-27
- **Phase:** 19.5

## Context

Design 38 §6 requires that "a payer-scoped user sees only their payer's policies", and 19.5's acceptance
criterion sharpens it: a cross-payer id must return **403, not an empty list**.

Nothing in the platform knew what a user's payer was. The access token contract has been **frozen** since
phase 17 (`docs/security/token-contract.md`) and carries no payer claim, so the restriction had to be resolved
somewhere other than the token.

## Decision

### The assignment lives in admin-service, beside role bindings and branch assignments

`admin.user_payer_assignment` (migration 0006) mirrors `user_branch_assignment` (14.2), whose own header states
the reason: *"assignment is an identity/administration concern, so it lives in admin-service alongside role
bindings"*. `payer_id` is a logical reference to `policy.payer` — a value, exactly as `branch_id` references
`provider.branch`.

The alternative — a table in the policy schema, where a real FK to `payer` is available — was rejected because
it would split a user's entitlements across two services, and the phase-16 access review enumerates them from
admin. **An entitlement the access review cannot see is an entitlement nobody revokes.**

### It is modelled as a RESTRICTION, not as an entitlement

No assignment ⇒ payer-**unrestricted**. An assignment ⇒ restricted to those payers.

The opposite reading — nobody sees anything until granted a payer — would have required assigning every
existing Beneficiary-Management officer to every payer on the day this shipped, and a grant that must be given
to everyone stops being read as a grant. Restricting a user is the deliberate, audited act; a revoke that
removes the user's LAST restriction is audited at `High` severity, because it is the largest single widening
of access this table can produce.

### "Could not ask" is a third state, and it denies

This is where the model earns its keep. "No rows" and "the directory is unreachable" are the same shape on the
wire and opposite in meaning. Reading the second as unrestricted would make an **admin-service outage silently
widen every restricted user to every payer** — a failure that looks like nothing and leaks one donor's caseload
onto another donor's screen.

So `PermittedPayers` has three values — `Unrestricted`, a restricted set, and `DenyAll` — and the HTTP
directory returns `DenyAll` on any failure. Note this is the *opposite polarity* to branch scope, whose empty
permitted set already denies; the same "fail closed" phrase means different code in the two cases, which is
precisely why it is written down here.

Resolution is cached ≤60s per user, matching the branch directory: a revocation takes effect within the TTL,
and admin-service stays off the critical path of every query.

### The scope is a PREDICATE inside the query, including the count

Every list applies the restriction inside the SQL that builds the page — not as a filter over the results.

The row **count** is the reason. A filter applied after the fact must be remembered on every new query, every
new sort, and on the total; the total is the one people forget. A total of 4 000 beside a page of 25 rows tells
a payer-restricted user exactly how large another payer's book of business is — the fact the restriction
existed to withhold.

### A targeted out-of-scope read is 403, not 404 or an empty page

A deliberate inversion of the usual "don't confirm existence" advice. That advice protects a resource whose
existence is the secret — typically a **person**. A payer is an **organisation Mersal contracts with**; its
existence is not secret, its members are.

Answering "no such policy" to an administrator looking straight at the policy number sends them to raise a
data-loss incident over what is actually a permission setting. Denials are audited as `PayerScopeDenied` at
`High`.

A policy with **no** payer (the pre-19.2 rows the 19.7 backfill retires) is readable only by an unrestricted
caller: a restricted user asked for one payer's book of business, and a row that might belong to any payer is
not it.

## Consequences

- **Branch scope is resolved on demand in member query, not in middleware.** Design 38 §6 makes
  policy-administration roles member-scoped (all branches), so narrowing every route in policy-service would
  enforce a boundary the surface does not have. A member LIST is the one place an operational role could sweep
  beyond their branch, so that is where it is applied.
- **A NULL branch is not excluded by branch narrowing, but IS excluded by a named branch filter.** Branch scope
  exists to keep one branch's worklist out of another's; a member search is not a worklist, and hiding every
  pre-0013 member from the receptionist trying to find them would break the counter to enforce a boundary the
  row does not even cross. "Members enrolled at Maadi", by contrast, is a question a NULL genuinely does not
  answer.
- **The 360 withholds sections, not the whole record.** A beneficiary may hold memberships under several
  payers. Refusing the entire 360 would hide the fact that other cover exists — which is exactly what an
  officer needs in order to route the question to someone who may see it. Withheld sections are counted and
  named.
- Payer scope is now available to 19.5b's extracts and 19.6b's dashboard as a single primitive
  (`libs/authz/PayerScope.cs`), which is what keeps the three surfaces from disagreeing about who may see what.

## Open

- No role is payer-scoped **by default**. Which real users get restricted is an operational decision that lands
  with the 19.7 role rollout; until somebody is assigned, the mechanism is inert by design.
- The 60-second directory cache is a revocation lag. It matches branch scope and is a deliberate trade against
  a round trip per request; a revocation that must be immediate needs a session kill, which is phase 17's
  surface, not this one.
