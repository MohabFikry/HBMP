# ADR-0018 — Append-only signed notes on policies and members (Phase 19.3)

- Status: Accepted
- Date: 2026-07-28
- Deciders: HBMP platform / benefit administration
- Context docs: `38-policy-member-administration.md §5.5`, `19-audit-strategy.md`, `11-permission-matrix.md`,
  `18-security-model.md`, `20-compliance-checklist.md`.

## Context

Administrators need to record why something was done: why a member was terminated, why a policy was renewed on
unusual terms, why an exception was allowed. These notes are read months later, frequently by someone else,
sometimes in a dispute and occasionally by a regulator.

That makes them evidence, and evidence has a property ordinary application data does not: **its value comes
from not having been changed.** A note that can be edited answers "what does the record say now"; only a note
that cannot be edited answers "what did this person assert at the time".

The problem is that assertions do get withdrawn. Somebody records a note against the wrong member, or states
something they later find was mistaken. A system that only appends and never retracts forces people to leave
known-false statements standing, which corrupts the record just as effectively as editing would.

## Decision

**Notes are append-only and signed. A note is never edited and never deleted; it is CANCELLED, and the
cancellation is itself a recorded act.**

- `note` rows are insert-only. There is no update path for `body`, and the database enforces it.
- Every note carries its author (`authored_by`), the exact time, and a **visibility class**
  (`Administrative` / `Financial` / `Clinical` / `Restricted`).
- Cancelling requires a **mandatory reason** and records who cancelled it and when. The note remains fully
  visible, rendered struck-through with a four-cue status treatment — never hidden, never collapsed by
  default, never filtered out of the list.
- A correction is a **new note** that supersedes the old one (`supersedes_note_id`), so both statements and
  their order survive.
- Cancelling **another user's** note requires `policy:supervise` — the supervisory increment, not something
  every officer holds.
- The **body** is projected by visibility class, not by role list: a Finance or Call-Centre reader receives a
  `Clinical` or `Restricted` note as existence + type + author + date with the body withheld and a stated
  reason. The note's presence is never concealed; only its content is.

## Consequences

- The list grows and is never pruned. Accepted: the volume is small relative to clinical data and the
  alternative is a record that cannot be relied upon.
- A cancelled note stays on screen. Operators initially read this as clutter; it is the point. A record that
  quietly drops withdrawn statements cannot show that a statement was ever made — which is exactly what a
  dispute turns on.
- Withholding a body rather than hiding the note means a Finance user can see that a clinical note exists and
  ask the right person, instead of concluding that nothing was recorded.
- The visibility projection is server-side. The client renders a "Restricted — clinical note" locked state
  from the withheld flag; the body is not in the payload, so it is not in the DOM.

## Alternatives rejected

- **Editable notes with an audit trail.** The audit log then holds the evidence and the note holds a summary,
  so the two disagree and only one of them is on screen.
- **Soft-delete (hide on cancel).** Removes the ability to show that an assertion was made and withdrawn,
  which is the case a note most often has to answer.
- **Role-based body filtering.** A role list drifts every time a role is added; a class attaches the rule to
  the CONTENT, so a note written today is still correctly withheld from a role invented next year.
