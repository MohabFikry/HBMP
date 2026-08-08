# Deferred findings — looked at, deliberately not fixed

Findings discovered while doing something else, judged real, and **not** fixed in the change that found
them. They are here so that "we know about it" is a written fact rather than someone's memory.

This register is not a backlog of ideas. An entry qualifies only if all three hold:

1. It is a **divergence from a design document or a stated invariant**, not a preference.
2. It was **verified against the code** — the entry names the file and the line.
3. It was left open for a reason that is written down. "No time" is not one of those reasons on a
   pre-production platform with no time pressure; "this widens access and is the sponsor's call",
   "this is a feature in its own right", or "the fix needs a decision we do not have" are.

**How an entry leaves.** It is fixed and the entry is deleted in the same commit, with the test that
closes it named in `invariant-registry.yaml`. Or it is retired by a written decision — an ADR, or a
design-doc amendment — and the entry is deleted citing it. An entry that is merely old does not leave.

---

## DF-1 — a claim cannot be corrected before it is submitted, by anyone

**Found:** 2026-07-31, while closing the provider-portal read gaps (`ec82d79`, `e7e4ec2`).
**Source:** `11-permission-matrix.md` §3.4, Provider Admin row: `claim C🟠PO R🟠PO **U🟠PO(pre-submit)**`.
**Severity:** Low — nothing is exposed and nothing is mis-paid. It is a capability the matrix grants
and the platform does not serve.

The matrix gives a provider an UPDATE on its own claim while it is still pre-submit — the ordinary
"I typed the wrong quantity, let me fix it before you look at it" correction. There is no update
endpoint for a claim or a submission anywhere in `services/claims/Api`: the surface is
`POST /api/v1/claims/submissions` (file it), `POST …/documents` (attach to it), and the reads added in
`e7e4ec2`. `SubmissionEndpoints.cs` has no `MapPut`/`MapPatch`, and neither does `ClaimsEndpoints.cs`.

**Why this is deferred rather than fixed.** It is not an authorization divergence — the case the
provider-portal work was closing. No role can do this: not the provider, not a claims officer
correcting a submission on a provider's behalf. So it is an absent feature, and building it is a
product decision with a shape to settle first:

- Is the editable thing the **submission** (the provider's assertion) or the **claim** (the platform's
  record derived from it)? Editing the claim means re-running intake matching and re-pricing; editing
  the submission means deciding what happens to the claim already created from it.
- What is "pre-submit"? A submission creates its claim immediately, so there is no draft state today.
  Adding the verb means adding the state, and a state a claim can sit in un-adjudicated is a state the
  ageing and reconciliation reports have to know about.
- The alternative already exists and may be the right answer: an append-only **adjustment**, which is
  how every other correction on this platform is recorded, and which leaves the original assertion
  intact. A silent edit of a financial claim is exactly what the append-only design refuses elsewhere.

**To close:** either build the draft/edit path with the state machine and the report changes it implies,
or amend §3.4 to drop `U🟠PO(pre-submit)` and record in an ADR that corrections are adjustments here.
The second is cheaper and is probably right; it is still a decision, not a tidy-up.
