-- approvals-service — 0004: the visit an authorization came from (ADR-0031).
--
-- An authorization already records WHO ordered the thing (`created_by`, so the decision notice has an
-- addressee) and WHAT it was raised against (`source_ref` — an order-line or prescription id). It recorded
-- nothing about the VISIT, so the one artefact in the platform that can hold a consultation open for days had
-- no way back to the consultation. The appointment's episode timeline could show "sent for approval" and then
-- nothing at all: the desk saw the wait begin and never saw it end.
--
-- NULLABLE, and it stays nullable. A manual authorization is raised by a reviewer with no encounter in hand,
-- and every row written before this column existed has no honest value to backfill. A default here would be a
-- guess, and a guessed encounter puts one member's authorization on another member's timeline — which is
-- worse than the gap it would be papering over.
--
-- No index: nothing looks an authorization up BY encounter. The column travels outward on the decision event
-- so emr can attach the step; it is a correlation key on the wire, not a query key here.

ALTER TABLE approvals.authorization ADD COLUMN IF NOT EXISTS encounter_id uuid;

COMMENT ON COLUMN approvals.authorization.encounter_id IS
    'The encounter the authorized order/prescription was raised in (ADR-0031). NULL for manual authorizations '
    'and for rows predating the column. Carried on the decision events so emr can step the decision onto the '
    'patient''s care episode.';
