-- pharmacy-service — 0014 CONTRACT: prescription_line.root_line_id becomes NOT NULL. DEFERRED.
--
-- ⚠ NOT applied by tools/ci/apply-migrations.sh. Apply with: tools/ci/apply-deferred-migrations.sh
--
-- The medication twin of orders deferred/0014 — read that file's header for the reasoning. Same column, same
-- rolling-deploy hazard, same precondition: the 30.1 deploy fully rolled out to every replica of
-- pharmacy-service before this runs. A NOT NULL applied too early is a prescription a doctor cannot write.

BEGIN;

UPDATE pharmacy.prescription_line SET root_line_id = prescription_line_id WHERE root_line_id IS NULL;
ALTER TABLE pharmacy.prescription_line ALTER COLUMN root_line_id SET NOT NULL;  -- migrate-compat: contract-ok (post-rollout contract step; see header)

COMMIT;
