# ADR-0022 — The entity timeline is a replayable projection over the audit stream, not a second log

- **Status:** Accepted
- **Date:** 2026-07-27
- **Phase:** 19.3c

## Context

Design 38 §5c asks for a single chronological view answering *what happened to this policy / this member, when,
and who did it*. The obvious implementation — a `history` table every writer appends to — is the wrong one.

A hand-maintained log drifts from the audit trail the moment one write path forgets to append, and a history
that disagrees with the audit trail is **worse than no history**: it looks authoritative and is quietly wrong.
Nobody finds out, because finding out requires comparing two things nobody compares.

## Decision

The timeline is a **projection** derived from the hash-chained `audit_event` stream and the domain events
19.2/19.3/19.3b already emit. Nothing in the domain writes to it as part of doing its work. It can be discarded
and rebuilt at any time.

### It lives in policy-service, not reporting-service

The build prompt says to pick one and justify it. policy-service, because:

- **The scope refs are `policy_id` and `enrollment_id`**, which this service owns. In reporting-service they
  would be opaque ids requiring a lookup back into policy for every render.
- **The diff projection reuses the visibility rules already here** (19.3 notes, 19.3b documents).
  Reimplementing that redaction in another service is precisely the second-redaction-path the design forbids —
  and the second one is the one that gets a clinical field wrong.
- **reporting-service is a de-identified aggregate read model by design.** Its `financial_fact` has no
  diagnosis column and a test asserts that against `information_schema`. Per-member, class-projected diffs are
  the opposite of that property; putting them there would quietly turn a de-identified store into an
  identifiable one, and the test that guards it would still pass.

### Determinism is the mechanism, not a nicety

`TimelineProjection.EntryIdFor` derives the primary key from a SHA-256 of the source event id rather than
generating a random one, and the diff serializer sorts its keys.

Together these make a rebuild produce **byte-identical rows**. That matters because "replayable" without
determinism means "produces a similar-looking history with different ids" — which no comparison can verify, so
the only check on a rebuild would be eyeballing it. `TimelineReplayTests` asserts field-by-field equality
across a rebuild instead.

Idempotency is held at three layers: the derived id, an in-code dedupe (which also collapses duplicates *within*
a batch), and `UNIQUE(source_event_id)` as the backstop when two projector instances race. At-least-once
delivery makes re-delivery normal rather than exceptional, and a duplicated line in someone's history reads as
the same thing having happened twice.

### Append-only, with one narrow exception

A timeline entry is never edited; a correction is a new entry referencing the original. DELETE is refused too —
**except inside a declared rebuild**, signalled by the session GUC `app.timeline_rebuild`.

The asymmetry is deliberate: discarding *all* derived data and re-deriving it is safe in a way that quietly
removing one inconvenient line is not. Requiring an explicit flag makes a rebuild a decision somebody made,
visible in the connection's own state, rather than something a stray `DELETE` achieves. (`SET LOCAL` is scoped
to a transaction, so the rebuild opens one — outside a transaction it is silently a no-op and every rebuild
would fail with the append-only error, which is what the guard should do to everything that is not a declared
rebuild.)

### Access events are part of the story

Who viewed a restricted document, or invoked break-glass, belongs on the member's timeline (design 19) — and is
frequently the most important line on it.

### Diffs are minimized and class-projected at READ time

Only fields that actually changed reach `change_diff`; a diff carrying every field of a row is a *copy* of the
row, and a timeline of copies is a second database of PHI with none of the controls the first one has.

The class projection happens at read rather than being stored redacted, because a stored-redacted diff would
have to be re-stored every time a role's entitlement changed — and the copy already written would still hold
the values. An operational role sees *that* a clinical record changed, with actor and timestamp, never what it
says. A withheld diff is explicitly flagged so the UI renders "details restricted for your role" rather than a
blank row that reads as nothing having happened.

## Consequences

- The timeline can lag. It is eventually consistent with the audit stream by construction, and a rebuild is the
  repair — which is why the rebuild is a first-class, tested operation rather than a script.
- An unmapped event type still produces an entry, categorized `Administrative`, with the raw type as its
  summary. Dropping it would leave a hole in the history with no trace that anything was missing — the worst
  failure mode for a record whose purpose is completeness.
- Bilingual summaries are authored as a table, not machine-translated at render time. A timeline an
  Arabic-speaking officer cannot read is not a timeline.
- Exports carry no diffs at all, whatever the caller's role: an export leaves the platform's controls behind
  and becomes a file on somebody's laptop, so it takes the narrower rule rather than the caller's entitlement.

## Open

- The live wiring from the audit stream and the event bus into `TimelineProjector` lands with the shared event
  bus, like the other fan-out consumers (phases 5–8). Until then the projector is driven by tests and by the
  rebuild path; the projection logic and its guarantees are complete and proven.
