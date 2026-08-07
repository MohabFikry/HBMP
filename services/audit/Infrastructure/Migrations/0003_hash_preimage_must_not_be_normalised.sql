-- audit-service — 0003: before_state / after_state become TEXT, because they are HASH PRE-IMAGE.
--
-- ============================================================================================================
-- THE DEFECT
-- ============================================================================================================
-- These two columns were declared `jsonb` in 0001. jsonb is not a string type — it is a PARSED representation,
-- and Postgres re-renders it on read: it inserts a space after every `:` and SORTS OBJECT KEYS.
--
-- `record_hash` is computed at INGEST, over the compact JSON the emitting service wrote (System.Text.Json
-- emits no spaces and preserves property order). The verifier recomputes it over whatever Postgres hands
-- back. Those are different strings, so the hashes differ, and AuditVerifier reported
--
--     integrity.mismatch ... record_hash mismatch ... (record was tampered)
--
-- for records nobody had touched. THE STORAGE LAYER WAS REWRITING THE THING THAT WAS HASHED.
--
-- Proved rather than inferred: recomputing the hash of audit_event 0d9945de-…-7e697acac65f with the COMPACT
-- JSON reproduces its STORED hash 29f901b4…4482c exactly. Only the true pre-image reproduces a SHA-256, so
-- that record was demonstrably intact. See JsonbNormalisationHypothesisTests.
--
-- ============================================================================================================
-- WHY THIS IS SERIOUS IN BOTH DIRECTIONS
-- ============================================================================================================
-- The obvious harm is the false alarm. The real harm is what a standing false alarm does to a control: an
-- integrity verifier that cries "tampered" on healthy data is one people stop reading, and it is the only
-- mechanism that would tell them about REAL tampering. A detector nobody believes is worse than none,
-- because it is still counted as coverage.
--
-- ============================================================================================================
-- WHAT THIS MIGRATION DOES NOT FIX
-- ============================================================================================================
-- 322 rows already carry a normalised pre-image. Of those, 248 are single-key objects whose original bytes
-- differ only by the added space and are recoverable in principle; **75 are multi-key objects whose KEY ORDER
-- jsonb discarded on write. Their pre-image is gone and they can never be re-verified.**
--
-- Those 75 are NOT repaired here, and must not be: rewriting a hash-chained row to make a verifier pass is
-- precisely the tampering the chain exists to detect, and it would be indistinguishable from an attacker
-- doing the same thing. They are recorded as a known, dated discontinuity instead —
-- docs/audit-chain-integrity-2026-08.md — which is what an evidential trail does with damage it cannot undo.
--
-- The cast below preserves what is currently stored; nothing further is lost by it.

ALTER TABLE audit.audit_event
    ALTER COLUMN before_state TYPE text USING before_state::text,
    ALTER COLUMN after_state  TYPE text USING after_state::text;

COMMENT ON COLUMN audit.audit_event.before_state IS
    'HASH PRE-IMAGE — TEXT, never jsonb. Stored byte-for-byte as the emitting service wrote it, because '
    'record_hash is computed over this exact string. A normalising type (jsonb) re-renders it on read and '
    'makes AuditVerifier report intact records as tampered (migration 0003).';

COMMENT ON COLUMN audit.audit_event.after_state IS
    'HASH PRE-IMAGE — TEXT, never jsonb. See before_state.';
