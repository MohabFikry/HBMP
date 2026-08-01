# Call-centre portal redesign — design

**Date:** 2026-08-01
**Status:** approved, implementing
**Scope:** `services/callcentre`, `apps/web` (call-centre screens + the shared patient profile), `libs/authz` (docs only)

## Why

The call centre's role in the operation has changed. Caller identity is now confirmed **on the phone**, by the
agent, before or during the conversation — it is not a step the system administers. The portal was built around
the opposite assumption: an on-screen challenge (`≥2` identifier types) gated every disclosure, and everything
downstream — masking, the attempt cap, the expiry — existed to make that challenge honest.

This redesign moves the system from *administering* the check to *recording* it, and cleans up the four
consequences that follow.

> **This removes verify-before-disclose**, which the service README, its class documentation and several test
> classes are built around. That is a deliberate operational decision, not an oversight. The documentation is
> rewritten as part of this work rather than left asserting a control that no longer exists — a doc that
> describes a guarantee the code stopped providing is worse than no doc.

## 1. Verification becomes an attestation

The gate's **shape** is retained: a disclosure requires a `Passed` verification bound to *this interaction* and
*this beneficiary*. Only the thing that satisfies it changes.

| Before | After |
|---|---|
| Agent ticks ≥2 identifier types, presses Pass | Opening a member's file records one attestation |
| 3 failed attempts → `429 verification-locked` | removed — nothing can fail |
| 60-minute TTL from `VerifiedAt` | removed — bounded by the call |
| `MemberMatch.MaskedMemberNo` (`•••001`) | `MemberMatch.MemberNo`, in full |
| `MemberMatch.ChallengeableIdentifierTypes` | removed |

### Why the TTL and the cap go

Both were properties of a **challenge**, not of the gate:

- The **TTL** existed because evidence from a challenge decays — an answer given 90 minutes ago is weak proof
  about who is on the line now. An attestation that the agent is *on the phone with this person* does not decay
  over the call; it ends when the call ends. That boundary is the interaction, which is already enforced
  (`Status == Open` plus the beneficiary binding).
- The **cap** counted failed attempts so a caller could not probe which identifiers a record holds, one
  `Failed` at a time. With no challenge there are no attempts, and a cap on zero is decoration.

Retaining either would produce a silent mid-call `403` for a reason the agent could neither see nor fix.

### The record stays honest about the past

New DDL `0006_verification_method.sql` adds to `callcentre.caller_verification`:

```sql
ALTER TABLE callcentre.caller_verification
    ADD COLUMN IF NOT EXISTS method text NOT NULL DEFAULT 'OnSystem';
```

New rows write `'OffSystem'`. Existing rows keep `'OnSystem'` — they *were* on-system challenges, and
back-dating them into attestations would misreport what the platform did. Expand-only, backward compatible,
applied by hand per `docs/runbooks/deploy-and-rollback.md`.

`VerifiedIdentifierTypes` is empty for off-system rows: the agent confirms identity verbally and does not report
which identifiers they used, so recording a guess would be fabrication.

### Mechanism

One mechanism, not two. The client POSTs the attestation when the agent opens a member's file; the existing
server gate is satisfied by it. If the POST fails the file does not open and the agent sees the error — the same
failure mode as any other write, rather than a silent unlocked-or-not ambiguity.

```
select search result
   → POST /call-interactions/{id}/verification { beneficiaryId, result: "Passed", method: "OffSystem" }
   → GET  /call-centre/members/{beneficiaryId}/summary?interactionId={id}
```

`profile-service` needs no change: `ProfilePolicies.RequiresCallCentreVerification` stays true for call-centre
principals and is satisfied by the attestation, so the ADR-0026 contract holds as written.

### The profile stays call-bound

A profile read still carries an `interactionId`, so **every PHI read keeps its audit link to a call**. Removing
the challenge does not mean removing the reason a record was opened.

## 2. Search — one bar, no type picker

`CallCentreBooking.tsx` drops `SEARCH_BY`, its `Select`, and the `searchBy`/`chosen` state. Both call-centre
screens move to a shared `MemberSearch` component so the workspace and the booking journey cannot drift.

**No backend search change.** `PostgresReceptionIndex` already matches member number, national ID, passport,
refugee ID, UNHCR number, phone and name (single- and multi-word). The picker never narrowed the match — it only
set the keypad and the example, which its own help text admitted. Two text fixes:

- the search label and help name what is actually matched, including **name**;
- `Members.cs`'s `q-required` detail lists name, which it omits today.

"Card number" here means the number printed on the Mersal card — `MemberNo` (`MRS-M-2026-000005`), which is what
the reception index means by `Card`. `patient.beneficiary.card_number` is a separate field that the eligibility
projection does not carry; indexing it is out of scope for this change.

## 3. Patient profile access

Unchanged server projection. `call_center` already resolves to a real row in the design-39 §4 matrix: identity,
coverage, referrals, documents, financial, timeline and **full** call history; no allergies and no clinical
sections. No `ProfilePolicies` change, so no minimum-necessary widening.

What changes is reachability. Both call-centre profile links are `<a href>`, which **full-page-reloads the SPA**
— the open call, the search results and the selection are destroyed by the navigation. They become react-router
`Link`s.

## 4. Notes and call summary are one field

One control, labelled **Call summary**, written to `summary`. `notes` stops being collected.

This loses nothing: `CallHistoryProjection` — the profile-facing view other roles read — already projects
`Summary` and never `Notes`. Historical notes stay readable through the call-centre's own API. The effective cap
becomes `CallSummaryRules.MaxLength` (500), and `CcApi.close` drops to three arguments.

The two fields existed so that widening the audience for call history would not silently widen the audience for
an agent's mid-call working text. With one field there is no working text to protect: everything the agent types
is the operational account, and the label says so.

## 5. Back button that preserves state

Two problems, two fixes.

**The button.** `PageHeader` gains an optional `back` prop — one component, so it appears in every portal at
once. Profile links navigate with `state: { from }`; back goes there, falling back to `navigate(-1)`.

**The state.** React unmounts the origin screen on navigation, so history alone re-mounts an empty one. A
`useRestorableState` hook backed by `sessionStorage`, keyed per screen, persists the state worth returning to —
and survives a hard reload, which `location.state` does not.

Applied to the six screens that link into the profile — but only three of them turned out to hold state worth
restoring, and saying so is more useful than a uniform claim:

| Screen | Restored | Why |
|---|---|---|
| `CallCentre` (workspace) | open call, query, open member, outcome, summary | losing the call is the fault this exists for |
| `CallCentreBooking` | open call, query, open member, summary | same, mid-booking |
| `ReceptionDesk` (appointments) | `when`, custom `from`/`to`, `status`, `query` | a narrowed day board is real work to rebuild |
| `ClinicianWorklists`, `DoctorVisits` | nothing — they hold no filter state | the list re-fetches; returning to it *is* the previous page |
| `BeneficiaryPortal` | nothing — the link sits on a registration result | the record is already created; there is no draft to resume |

All six get `useOpenProfile`, so Back works from every one of them. Only where a screen holds state the user
built by hand does `useRestorableState` earn its keep; adding it to a screen that re-fetches everything would be
ceremony.

## Out of scope

- **Visual restyling.** Every item above is functional; this stays inside the existing design system.
- **Indexing `patient.beneficiary.card_number`** in the eligibility reception projection.
- **Widening the call-centre profile row** to clinical sections.

## Testing

- Attestation records `method='OffSystem'`, binds the interaction, and opens the 360.
- The interaction binding still refuses a 360 for a beneficiary the call was never opened against.
- No lockout: repeated opens all succeed.
- No TTL: an attestation older than 60 minutes still discloses while the interaction is Open.
- A closed interaction still refuses — the one expiry that remains.
- Search matches name, phone and member number through one field with no type selected.
- Back from the profile returns to the origin screen with its state intact, including an open call.
- axe/keyboard/RTL pass on both redesigned screens.
