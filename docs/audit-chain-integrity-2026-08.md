# Audit chain integrity — the 2026-08 `jsonb` pre-image defect

- **Status:** root cause found and fixed forward; 75 historical records permanently unverifiable
- **Found:** 2026-08-07, from `crit: integrity.mismatch` in audit-service on restart
- **Fix:** `services/audit/Infrastructure/Migrations/0003_hash_preimage_must_not_be_normalised.sql`
- **Guard:** `Mersal.Audit.Tests.HashPreimageStorageTests`
- **Evidence:** `Mersal.Audit.Tests.JsonbNormalisationHypothesisTests`

## What was reported

```
crit: integrity.mismatch in audit partition 202607: broken at index 28
      (record 0d9945de-46f5-45ff-875d-7e697acac65f) — record_hash mismatch:
      stored 29f901b4…4482c, recomputed bc349272…eaeadac (record was tampered)
crit: integrity.mismatch in audit partition 202608: broken at index 65 …
```

Both partitions. The verifier returns on the first break, so those indices are the first affected record in
each partition, not the only ones.

## Root cause

`audit.audit_event.before_state` and `after_state` were declared **`jsonb`** in migration 0001.

**jsonb is not a string type.** It is a parsed representation, and Postgres re-renders it on read: it inserts
a space after every `:` and **sorts object keys**.

`record_hash` is computed at **ingest**, over the compact JSON the emitting service wrote — `System.Text.Json`
emits no spaces and preserves property order. `AuditVerifier` recomputes it over whatever Postgres hands
back. Those are different strings, so the hashes differ.

**The storage layer was rewriting the thing that had been hashed.**

### Proved, not inferred

Recomputing the hash of `0d9945de-…-7e697acac65f` with the **compact** JSON reproduces its **stored** hash
`29f901b471db416197dd97aedc28abb2978be947d053f6d558e7793e9644482c` exactly. Only the true pre-image
reproduces a SHA-256, so that record was demonstrably **intact**. Recomputing with Postgres's rendering
reproduces the verifier's `bc349272…` — the alarm, reconstructed from both sides.

The whole difference is one space:

```
written:   {"caseNo":"CASE-EDITED-0523"}
read back: {"caseNo": "CASE-EDITED-0523"}
```

## Why this mattered in both directions

The obvious harm is the false alarm. The real harm is what a standing false alarm does to a control: an
integrity verifier that reports "tampered" on healthy data is one people stop reading — and it is the only
mechanism that would tell them about **real** tampering. A detector nobody believes is worse than no
detector, because it is still counted as coverage.

## Blast radius

| | Rows |
|---|---|
| Total audit records | 33,407 |
| Records carrying `before_state` / `after_state` | **322** |
| — single-key objects (differ only by the added space) | 248 |
| — **multi-key objects: key ORDER discarded on write** | **75** |
| Partitions affected | 2 of 2 (`202607`, `202608`) |

## What is fixed, and what is not

**Fixed forward.** Migration 0003 changes both columns to `text`, so Postgres stores them byte-for-byte as
written. Every record ingested from now on verifies correctly.

**The 75 multi-key records can never be re-verified.** jsonb discarded their key order at write time; the
pre-image no longer exists anywhere. No amount of later cleverness recovers it.

**They have deliberately NOT been repaired.** Rewriting a hash-chained row so a verifier passes is precisely
the tampering the chain exists to detect, and it would be indistinguishable from an attacker doing the same
thing. The correct response to damage an evidential trail cannot undo is to record it — dated, explained and
scoped — which is what this document is.

The 248 single-key records are recoverable in principle by removing the added space, and have also been left
alone, for the same reason: a partial, silent repair of a hash chain is worse than a documented gap.

## Consequence for anyone reading the trail

For records in partitions `202607` and `202608` that carry a `before_state` or `after_state`:

- A `record_hash` mismatch on such a record is **explained by this defect** and is not by itself evidence of
  tampering.
- Chain **linkage** (`prev_hash`) was never affected — insertion, deletion and reordering would still have
  been detected throughout, and were not.
- Records without a state field — 33,085 of 33,407 — are unaffected and verify normally.

Any investigation touching that window should cite this document, and should treat the 75 multi-key records
as having no verifiable content hash.

## Prevention

`HashPreimageStorageTests` fails the build if `before_state` or `after_state` is ever declared as a
normalising type (`jsonb`, `json`, `xml`) again, and a second test fails if the canonicalizer stops reading a
column the guard claims to cover — so the guard cannot quietly go stale.

It reads the **migrations**, not a live database, so it fails on the change that introduces the defect rather
than on the first verifier run afterwards, and it runs on a laptop with no Postgres. A schema check that
needs a database is a schema check that does not run.
