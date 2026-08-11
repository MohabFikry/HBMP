-- emr-service — 0015: rows that belong to no tenant are backfilled, then made impossible.
--
-- The same defect policy's 0016 repairs, in the schema where it left one row: appointment_queue held a
-- ticket with tenant_id = ''. TenantStampingInterceptor stamped the request tenant onto inserted rows and,
-- when there was no tenant to stamp, silently did nothing — so the entity's own
-- `public string TenantId { get; set; } = "";` default was persisted. NOT NULL is satisfied, the RLS policy
-- is correct and simply never matches, and the row belongs to nobody: invisible to every real tenant,
-- visible to any session binding an empty one.
--
-- One row is not a small version of this problem. It is a QUEUE TICKET — a person waiting to be seen whose
-- ticket no clinic can list, because every list is bound to a real tenant. It would have been noticed as
-- "the patient vanished from the board", not as a data-integrity issue.
--
-- The write path is fixed first (libs/data/TenantOwned.cs now throws rather than writing an unscoped row),
-- so this is repair and prevention, not a dam in front of a running tap.

-- WHY THE ORPHAN COUNT IS CHECKED BEFORE THE TENANT COUNT.
--
-- The refusal below is right: with two tenants, picking one to adopt an orphan row is a guess, and a guessed
-- tenant is one organisation's clinical record inside another's. But it was raised on the tenant count ALONE,
-- before asking whether there was anything to adopt — so a database with two tenants and zero orphans, which
-- is every correctly-behaving multi-tenant deployment, aborted here.
--
-- `apply-migrations.sh` runs under `set -e` and replays every file on every pass, so that abort stopped every
-- service after `emr/` alphabetically: orders, patient, pharmacy, policy, profile, provider, reporting. The
-- symptom is those services' tests failing on missing tables, several migrations away from the cause, on a
-- database that looks migrated. Guaranteed to appear the day a second tenant is onboarded — the exact event
-- this platform is built for.
--
-- So: count the orphans first. None ⇒ nothing to guess about, and the file proceeds to its second half, which
-- is the half that matters going forward — the CHECK constraints that make an unscoped row impossible. Some,
-- and more than one tenant ⇒ still refuse, unchanged, for the original reason.
DO $$
DECLARE
    sole    text;
    tenants int;
    moved   int;
    orphans int := 0;
    found   int;
    t       record;
BEGIN
    SELECT count(DISTINCT tenant_id), min(tenant_id) INTO tenants, sole
    FROM emr.appointment WHERE tenant_id <> '';

    FOR t IN
        SELECT c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables tb
          ON tb.table_schema = c.table_schema AND tb.table_name = c.table_name
        WHERE c.table_schema = 'emr' AND c.column_name = 'tenant_id' AND tb.table_type = 'BASE TABLE'
    LOOP
        EXECUTE format('SELECT count(*) FROM emr.%I WHERE tenant_id = %L', t.table_name, '') INTO found;
        orphans := orphans + found;
    END LOOP;

    IF orphans > 0 AND tenants > 1 THEN
        RAISE EXCEPTION 'this database has % tenants and % unscoped row(s); assigning them to one tenant '
                        'would be a guess. Attribute them from their own foreign keys instead.', tenants, orphans;
    END IF;

    -- The backfill runs only when there is something to move AND somewhere to move it. `sole IS NULL` means
    -- the schema holds no tenanted row to learn from, and the UPDATE below would then write NULL into a NOT
    -- NULL column — the original guarded that with a RETURN, which also skipped the constraints. Skipping the
    -- backfill alone is the narrower thing to skip: the constraints are prevention and are wanted either way.
    IF orphans = 0 THEN
        RAISE NOTICE 'no unscoped rows — skipping the backfill, still applying the constraints';
    ELSIF sole IS NULL THEN
        RAISE NOTICE 'no tenanted rows to learn the tenant from — nothing to backfill';
    ELSE
        FOR t IN
            SELECT c.table_name
            FROM information_schema.columns c
            JOIN information_schema.tables tb
              ON tb.table_schema = c.table_schema AND tb.table_name = c.table_name
            WHERE c.table_schema = 'emr' AND c.column_name = 'tenant_id' AND tb.table_type = 'BASE TABLE'
        LOOP
            EXECUTE format('UPDATE emr.%I SET tenant_id = %L WHERE tenant_id = %L', t.table_name, sole, '');
            GET DIAGNOSTICS moved = ROW_COUNT;
            IF moved > 0 THEN
                RAISE NOTICE 'emr.%: % row(s) assigned to the sole tenant', t.table_name, moved;
            END IF;
        END LOOP;
    END IF;

    FOR t IN
        SELECT c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables tb
          ON tb.table_schema = c.table_schema AND tb.table_name = c.table_name
        WHERE c.table_schema = 'emr' AND c.column_name = 'tenant_id' AND tb.table_type = 'BASE TABLE'
    LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conname = format('ck_%s_tenant_not_blank', t.table_name)
              AND conrelid = format('emr.%I', t.table_name)::regclass)
        THEN
            EXECUTE format('ALTER TABLE emr.%I ADD CONSTRAINT %I CHECK (tenant_id <> %L)',
                           t.table_name, format('ck_%s_tenant_not_blank', t.table_name), '');
        END IF;
    END LOOP;
END $$;
