-- pharmacy-service — 0020: the shortage the counter could not record. Design 49 §5.
--
-- ============================================================================================================
-- A FEATURE THAT EXISTED IN FOUR PLACES AND NOT IN THE DATABASE
-- ============================================================================================================
-- `POST /api/v1/prescriptions/{rxId}/lines/{lineId}/out-of-stock` has been complete since phase 6.3. It
-- consumes nothing, so the unfilled quantity stays available for a later visit; it publishes
-- `RxLineOutOfStock` to `pharmacy.events` and a second, notification-shaped copy addressed to the PRESCRIBER,
-- on a route that escalates to the pharmacy supervisor after eight hours; and it audits.
--
-- Nothing in the SPA called it. And the flag it was supposed to raise existed like this:
--
--     zPrescriptionLine (contract)   outOfStock: z.boolean()        <- a first-class field
--     PrescriptionPage.tsx           renders a chip; excludes the line from `fillable`
--     DispensableLineView (server)   -- absent --
--     HttpApiClient                  outOfStock: false              <- a literal
--     DevApiClient                   outOfStock: true               <- on one fixture
--
-- So the feature rendered in development, rendered in the tests, and could not render in production. The
-- fixture was not agreeing with a broken client; it was the ONLY implementation.
--
-- ============================================================================================================
-- WHY THE FLAG PERSISTS, WHEN THE ENDPOINT DELIBERATELY STORED NOTHING
-- ============================================================================================================
-- "No accumulator change — notify + audit only" is right about the accumulator and was right about the record
-- for as long as nothing could raise the flag. With a button in front of it, three things break:
--
--   * a reload loses it, so the chip the contract promises cannot survive a page refresh;
--   * the same line can be flagged five times, and the prescriber gets five actionable notifications with
--     five eight-hour escalations behind them;
--   * nobody can answer "what are we out of" — the question that turns a counter's problem into a purchase.
--
-- What does NOT change: `quantity_dispensed` is untouched, `quantity_remaining` is untouched, and
-- `prescription_line.status` is not written here. Out of stock is a fact about the PHARMACY, not about the
-- prescription — the line remains dispensable the moment stock arrives.
--
-- This is not an inventory count either. inventory-service owns stock levels and branch balances; this
-- records that a counter could not fill a line on a day. Different fact, different owner, different consumer.
--
-- EXPAND ONLY. Four nullable columns, no default, no backfill; a previous-build instance neither reads nor
-- writes them, and every row that exists today reads "never flagged", which is true.
ALTER TABLE pharmacy.prescription_line
    ADD COLUMN IF NOT EXISTS out_of_stock_at   timestamptz NULL,
    ADD COLUMN IF NOT EXISTS out_of_stock_by   text        NULL,
    ADD COLUMN IF NOT EXISTS out_of_stock_qty  numeric(14,3) NULL,
    ADD COLUMN IF NOT EXISTS out_of_stock_note text        NULL;

-- migrate-compat: contract-ok (the DROP is this file's own re-runnability, not a rollout step. Every
-- migration here re-runs on every pass — apply-migrations.sh keeps no applied-tracking table and runs under
-- `set -e` — so a bare ADD CONSTRAINT would abort the second pass and silently stop migrating every service
-- alphabetically after `pharmacy/`. Dropping the constraint this file itself added, immediately before
-- re-adding it, widens nothing: between the two statements the only rows that exist are ones the constraint
-- already admitted.)
ALTER TABLE pharmacy.prescription_line
    DROP CONSTRAINT IF EXISTS ck_rx_line_out_of_stock_complete;

-- WHO and WHEN travel together or not at all. A timestamp with no actor is a shortage nobody is accountable
-- for; an actor with no timestamp cannot be aged, and ageing is the whole point of a purchasing question.
-- The quantity and the note stay independently optional: "we have none at all" is the common case and needs
-- neither.
ALTER TABLE pharmacy.prescription_line
    ADD CONSTRAINT ck_rx_line_out_of_stock_complete
    CHECK ((out_of_stock_at IS NULL AND out_of_stock_by IS NULL)
        OR (out_of_stock_at IS NOT NULL AND out_of_stock_by IS NOT NULL));

-- The purchasing question — "what is the counter short of, oldest first" — over the flagged rows only. A
-- partial index because flagged lines are a small minority of a table that holds every line ever prescribed,
-- and the query never wants the rest.
CREATE INDEX IF NOT EXISTS ix_rx_line_out_of_stock
    ON pharmacy.prescription_line (out_of_stock_at)
    WHERE out_of_stock_at IS NOT NULL;

COMMENT ON COLUMN pharmacy.prescription_line.out_of_stock_at IS
    'When the counter reported it could not fill this line. NULL means never reported — not "in stock", '
    'which is inventory-service''s fact and not this table''s. Cleared when a dispense lands against the '
    'line, because stock arriving is the flag''s natural end and a chip that outlives the shortage is worse '
    'than no chip.';
COMMENT ON COLUMN pharmacy.prescription_line.out_of_stock_by IS
    'The pharmacist who reported it. Present exactly when out_of_stock_at is (CHECK above).';
COMMENT ON COLUMN pharmacy.prescription_line.out_of_stock_qty IS
    'How much could not be filled, in the line''s quantity_unit. NULL means the whole remaining quantity — '
    'the common case, stored as absent rather than as a copy of quantity_remaining, which would go stale.';
COMMENT ON COLUMN pharmacy.prescription_line.out_of_stock_note IS
    'The pharmacist''s free-text note to the prescriber. Never sent in the notification body — an inbox line '
    'is read by whoever holds the device, and what is out of stock is between the pharmacy and the prescriber.';
