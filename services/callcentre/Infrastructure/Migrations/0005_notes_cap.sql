-- callcentre-service — bound the agent's working notes at the datastore, matching the API cap.
--
-- WHY.
--
-- `summary` was capped twice — validated in the API and declared varchar(500) in 0004 — while `notes`, the
-- field an agent actually types into mid-call, was validated nowhere and stored as bare `text`. The asymmetry
-- was accidental: 0001 created `notes` before there was a summary to compare it against, and 0004 added the
-- careful column beside it without revisiting the careless one.
--
-- A cap here is not about disk. It is that `notes` is free text on a PHI-adjacent aggregate, reachable by any
-- authenticated agent on their own call, with no upper bound on a single write — the shape of thing that turns
-- a client bug or a paste into an unbounded row. The API now refuses over 4000 characters
-- (CallSummaryRules.MaxNotesLength); this is the same rule where it cannot be bypassed by a future caller that
-- forgets to ask.
--
-- NOT VALID on purpose: the constraint governs every INSERT and UPDATE from here on, but does not scan or
-- reject rows already written under the old, unbounded rule. Existing notes stay readable — a migration that
-- refuses to apply because of historical data is a migration that does not get applied.

ALTER TABLE callcentre.call_interaction
    DROP CONSTRAINT IF EXISTS call_interaction_notes_len;

ALTER TABLE callcentre.call_interaction
    ADD CONSTRAINT call_interaction_notes_len
    CHECK (notes IS NULL OR length(notes) <= 4000) NOT VALID;

COMMENT ON COLUMN callcentre.call_interaction.notes IS
    'The AGENT''S working text. Deliberately NOT promoted to other roles — see summary. '
    'Capped at 4000 characters (CallSummaryRules.MaxNotesLength) in the API and by '
    'call_interaction_notes_len here.';
