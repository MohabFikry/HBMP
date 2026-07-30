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

DO $$
DECLARE
    sole    text;
    tenants int;
    moved   int;
    t       record;
BEGIN
    SELECT count(DISTINCT tenant_id), min(tenant_id) INTO tenants, sole
    FROM emr.appointment WHERE tenant_id <> '';

    IF tenants > 1 THEN
        RAISE EXCEPTION 'this database has % tenants; assigning orphan rows to one of them would be a guess. '
                        'Attribute them from their own foreign keys instead.', tenants;
    END IF;
    IF sole IS NULL THEN
        RAISE NOTICE 'no tenanted rows to learn the tenant from — nothing to backfill';
        RETURN;
    END IF;

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
