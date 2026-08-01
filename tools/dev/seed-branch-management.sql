-- Local-dev seed for Branch Management (phase 25.8 · design 42).
--
-- PURPOSE: make every alert path DEMONSTRABLE. A screen whose empty state is the only state anyone has seen
-- is a screen nobody has actually reviewed — the licence chip, the reassignment worklist, the low-stock list
-- and the expiry quarantine each need real rows behind them before "it looks fine" means anything.
--
--   * a practitioner whose licence expires in 20 DAYS      → the amber "Expiring" chip + the 30-day sweeper
--   * a practitioner whose licence expired LAST WEEK       → the red "EXPIRED — cannot be booked" chip
--   * a practitioner on LEAVE next week                    → a roster exception that removes slots
--   * a small item catalogue across BOTH categories        → the Medical / Non-medical tabs
--   * one batch expiring INSIDE 30 DAYS                    → the expiry worklist
--   * one batch ALREADY EXPIRED with stock on hand         → the quarantine state (cannot be issued)
--   * one item BELOW its reorder level                     → the low-stock worklist
--
-- NOT a migration, and deliberately not in any service's Migrations folder: this is demo data for a
-- developer's machine and must never ride along with a schema change into an environment holding real
-- records. Run it by hand:
--
--   PGHOST=localhost PGPORT=55432 PGUSER=hbmp PGDATABASE=hbmp psql -f tools/dev/seed-branch-management.sql
--
-- Idempotent: re-running replaces the seeded rows rather than doubling them. The one exception is
-- stock_movement, which is APPEND-ONLY and cannot be deleted through the app role — the guard below skips
-- the whole block if it has already run, because "delete and re-insert" is exactly what that ledger forbids.

\set ON_ERROR_STOP on

DO $$
DECLARE
    tenant     CONSTANT text := '11111111-1111-1111-1111-111111111111';
    maadi      uuid;
    dokki      uuid;
    expiring   CONSTANT uuid := '25000000-0000-0000-0000-00000000e001';
    expired    CONSTANT uuid := '25000000-0000-0000-0000-00000000e002';
    onleave    CONSTANT uuid := '25000000-0000-0000-0000-00000000e003';
    gloves     CONSTANT uuid := '25000000-0000-0000-0000-0000000017e1';
    sutures    CONSTANT uuid := '25000000-0000-0000-0000-0000000017e2';
    toner      CONSTANT uuid := '25000000-0000-0000-0000-0000000017e3';
    b_soon     CONSTANT uuid := '25000000-0000-0000-0000-00000000ba01';
    b_gone     CONSTANT uuid := '25000000-0000-0000-0000-00000000ba02';
    today      CONSTANT date := current_date;
BEGIN
    SELECT branch_id INTO maadi FROM provider.branch WHERE branch_code = 'MAA' LIMIT 1;
    SELECT branch_id INTO dokki FROM provider.branch WHERE branch_code = 'DOK' LIMIT 1;
    IF maadi IS NULL THEN
        RAISE EXCEPTION 'seed: the six branches are not present — apply provider 0005_branch.sql first';
    END IF;

    -- ---- practitioners -------------------------------------------------------------------------------
    -- Three, each demonstrating one state of the licence chip. Licence numbers are obviously fake and
    -- prefixed SEED- so nobody mistakes one for a real Egyptian registration.

    DELETE FROM provider.practitioner_branch_assignment WHERE practitioner_id IN (expiring, expired, onleave);
    DELETE FROM provider.practitioner_specialty        WHERE practitioner_id IN (expiring, expired, onleave);
    DELETE FROM provider.practitioner                  WHERE practitioner_id IN (expiring, expired, onleave);

    INSERT INTO provider.practitioner
        (practitioner_id, tenant_id, user_id, practitioner_type, full_name_en, full_name_ar,
         license_no, license_expiry, status)
    VALUES
        -- 20 days out: inside the 30-day threshold, so the sweeper has something to announce and the chip
        -- something to render in amber.
        (expiring, tenant, 'seed-dr-hala', 'Doctor', 'Dr Hala Fouad', 'د. هالة فوري',
         'SEED-LIC-0001', today + 20, 'Active'),
        -- Already lapsed: the red chip, and the state that makes new bookings 422.
        (expired,  tenant, 'seed-dr-omar', 'Doctor', 'Dr Omar Adel',  'د. عمر عادل',
         'SEED-LIC-0002', today - 7,  'Active'),
        -- Comfortably valid, so "on leave" is demonstrably a ROSTER state and not a licence one.
        (onleave,  tenant, 'seed-dr-mona', 'Doctor', 'Dr Mona Saleh', 'د. منى صالح',
         'SEED-LIC-0003', today + 400, 'Active');

    INSERT INTO provider.practitioner_specialty (practitioner_id, specialty_code, is_primary) VALUES
        (expiring, 'GP', true), (expired, 'IM', true), (onleave, 'PED', true);

    INSERT INTO provider.practitioner_branch_assignment
        (assignment_id, practitioner_id, branch_id, valid_from, valid_to, status)
    VALUES
        (gen_random_uuid(), expiring, maadi, today - 365, NULL, 'Active'),
        (gen_random_uuid(), expired,  maadi, today - 365, NULL, 'Active'),
        -- Two clinics, so the clinics manager's cross-branch view has something to be cross-branch about.
        (gen_random_uuid(), onleave,  dokki, today - 365, NULL, 'Active');

    -- ---- roster: Dr Mona is on leave next week ---------------------------------------------------------

    DELETE FROM emr.roster_exception WHERE created_by = 'seed';

    INSERT INTO emr.roster_exception
        (exception_id, tenant_id, branch_id, practitioner_id, date_from, date_to, kind, reason, created_by, updated_by)
    VALUES
        (gen_random_uuid(), tenant, NULL, onleave, today + 7, today + 11,
         'Leave', 'Annual leave (seed data)', 'seed', 'seed');

    -- ---- inventory catalogue -------------------------------------------------------------------------
    -- Both categories, so the tabs are not empty and the batch/expiry columns can be seen appearing on one
    -- and absent from the other.

    DELETE FROM inventory.branch_item WHERE item_id IN (gloves, sutures, toner);
    DELETE FROM inventory.stock_batch WHERE item_id IN (gloves, sutures, toner)
        AND NOT EXISTS (SELECT 1 FROM inventory.stock_movement m WHERE m.item_id = inventory.stock_batch.item_id);
    DELETE FROM inventory.item WHERE item_id IN (gloves, sutures, toner)
        AND NOT EXISTS (SELECT 1 FROM inventory.stock_movement m WHERE m.item_id = inventory.item.item_id);

    INSERT INTO inventory.item
        (item_id, tenant_id, sku, name_en, name_ar, category, unit_of_measure,
         is_batch_tracked, requires_expiry, storage_condition, cold_chain, status, created_by, updated_by)
    VALUES
        (gloves,  tenant, 'SEED-GLV', 'Examination gloves (M)', 'قفازات فحص (وسط)', 'Medical', 'box',
         true, true, 'Room temperature', false, 'Active', 'seed', 'seed'),
        (sutures, tenant, 'SEED-SUT', 'Absorbable sutures 3-0', 'خيوط جراحية قابلة للامتصاص 3-0', 'Medical', 'each',
         true, true, 'Room temperature', false, 'Active', 'seed', 'seed'),
        (toner,   tenant, 'SEED-TNR', 'Printer toner (black)', 'حبر طابعة (أسود)', 'NonMedical', 'cartridge',
         false, false, NULL, false, 'Active', 'seed', 'seed')
    ON CONFLICT (item_id) DO NOTHING;

    -- Reorder levels chosen so ONE item lands below its threshold below and the others do not.
    INSERT INTO inventory.branch_item (branch_id, item_id, tenant_id, reorder_level, lead_time_days, is_stocked, updated_by)
    VALUES
        (maadi, gloves,  tenant, 10, 3, true, 'seed'),
        (maadi, sutures, tenant, 40, 7, true, 'seed'),     -- deliberately ABOVE what we receive → low stock
        (maadi, toner,   tenant,  2, 5, true, 'seed')
    ON CONFLICT (branch_id, item_id) DO UPDATE
        SET reorder_level = EXCLUDED.reorder_level, lead_time_days = EXCLUDED.lead_time_days;

    INSERT INTO inventory.stock_batch (batch_id, tenant_id, item_id, batch_no, expiry_date, created_by)
    VALUES
        -- 18 days out: inside the 30-day expiry worklist, still usable.
        (b_soon, tenant, gloves,  'SEED-B-SOON', today + 18, 'seed'),
        -- Already expired WITH stock on hand: the quarantine state. Issue is refused; only a reasoned
        -- write-off clears it. Without a row like this, "quarantined" is a word nobody has seen render.
        (b_gone, tenant, sutures, 'SEED-B-GONE', today - 5,  'seed')
    ON CONFLICT (batch_id) DO NOTHING;

    -- ---- the ledger ------------------------------------------------------------------------------------
    --
    -- APPEND-ONLY: there is no delete-and-reinsert here, because the whole point of the table is that there
    -- cannot be. The guard makes the block a no-op on a second run rather than pretending otherwise.

    IF NOT EXISTS (SELECT 1 FROM inventory.stock_movement WHERE idempotency_key LIKE 'seed:%') THEN
        INSERT INTO inventory.stock_movement
            (movement_id, tenant_id, branch_id, item_id, batch_id, kind, quantity, reason, actor, occurred_at, idempotency_key)
        VALUES
            -- Comfortably in stock, and the batch expires in 18 days → the expiry worklist.
            (gen_random_uuid(), tenant, maadi, gloves,  b_soon, 'Receipt',  60, NULL, 'seed', now() - interval '20 days', 'seed:glv-receipt'),
            (gen_random_uuid(), tenant, maadi, gloves,  b_soon, 'Issue',   -14, NULL, 'seed', now() - interval '3 days',  'seed:glv-issue'),
            -- 25 on hand against a reorder level of 40 → the low-stock worklist. The batch is also expired,
            -- so this one row demonstrates BOTH the low-stock and the quarantine states at once.
            (gen_random_uuid(), tenant, maadi, sutures, b_gone, 'Receipt',  25, NULL, 'seed', now() - interval '90 days', 'seed:sut-receipt'),
            -- Non-medical: no batch, no expiry — which is what the Non-medical tab must show.
            (gen_random_uuid(), tenant, maadi, toner,   NULL,   'Receipt',   6, NULL, 'seed', now() - interval '10 days', 'seed:tnr-receipt'),
            -- A stock-take that found one fewer than the books said. Recorded as a VARIANCE with a reason,
            -- never as an overwrite — so the ledger demonstrably explains its own balance.
            (gen_random_uuid(), tenant, maadi, toner,   NULL,   'Count',    -1, 'Annual stock-take (seed data)', 'seed', now() - interval '1 day', 'seed:tnr-count');
    END IF;

    RAISE NOTICE 'seed: branch management demo data applied (3 practitioners, 1 leave, 3 items, 2 batches)';
END $$;

-- Verify every alert path actually has something behind it. A seed that silently seeded nothing is worse
-- than no seed: the screens look correct and empty, and nobody discovers otherwise until a demo.
SELECT 'licence expiring within 30d' AS path, count(*) AS rows FROM provider.practitioner
    WHERE license_no LIKE 'SEED-%' AND license_expiry BETWEEN current_date AND current_date + 30
UNION ALL SELECT 'licence expired', count(*) FROM provider.practitioner
    WHERE license_no LIKE 'SEED-%' AND license_expiry < current_date
UNION ALL SELECT 'roster leave', count(*) FROM emr.roster_exception WHERE created_by = 'seed' AND NOT is_deleted
UNION ALL SELECT 'items (both categories)', count(DISTINCT category) FROM inventory.item WHERE sku LIKE 'SEED-%'
UNION ALL SELECT 'batch expiring within 30d', count(*) FROM inventory.stock_batch
    WHERE batch_no LIKE 'SEED-%' AND expiry_date BETWEEN current_date AND current_date + 30
UNION ALL SELECT 'batch expired (quarantined)', count(*) FROM inventory.stock_batch
    WHERE batch_no LIKE 'SEED-%' AND expiry_date < current_date
UNION ALL SELECT 'stock lines below reorder', count(*) FROM (
    SELECT bi.branch_id, bi.item_id, bi.reorder_level, COALESCE(SUM(m.quantity), 0) AS on_hand
    FROM inventory.branch_item bi
    LEFT JOIN inventory.stock_movement m ON m.branch_id = bi.branch_id AND m.item_id = bi.item_id
    JOIN inventory.item i ON i.item_id = bi.item_id AND i.sku LIKE 'SEED-%'
    GROUP BY bi.branch_id, bi.item_id, bi.reorder_level
) x WHERE x.on_hand <= x.reorder_level;
