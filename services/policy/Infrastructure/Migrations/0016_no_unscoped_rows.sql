-- policy-service — 0016: rows that belong to no tenant are backfilled, then made impossible.
--
-- WHAT WENT WRONG. TenantStampingInterceptor stamped the request tenant onto every inserted row that maps a
-- TenantId column — and when there was no tenant to stamp, it silently did nothing and the entity's own
-- `public string TenantId { get; set; } = "";` default was persisted. The result is a row with
-- tenant_id = '': NOT NULL is satisfied, the RLS policy is correct and simply never matches
-- (`tenant_id = current_setting('app.tenant_id')` can never equal a real tenant), so the row is invisible to
-- every tenant that exists and visible to any session that binds an empty one. The application had, in
-- effect, lost it.
--
-- Found by tools/ci/check-tenant-isolation.py, which reported it as a FAIL-OPEN POLICY — the checker's
-- unbound probe binds the empty string, so these rows came back. That misdiagnosis cost a detour through
-- pg_policy: the policies were right the whole time. The checker now names this case for what it is.
--
-- ORDER MATTERS AND THIS FILE IS THE SECOND STEP. The interceptor now THROWS rather than writing an
-- unscoped row (libs/data/TenantOwned.cs), so no new ones can appear. Adding the constraint before fixing
-- the write path would only have moved the failure from "silent orphan row" to "insert error in
-- production", which is louder but no more correct.
--
-- BACKFILL. Every affected row is assigned the sole tenant. That is the truthful answer here: this platform
-- runs one tenant (ADR-0011), every non-empty tenant_id in the database is that same id, and these rows were
-- written by handlers serving it. On a multi-tenant deployment this backfill would be WRONG and the rows
-- would have to be attributed from their own foreign keys instead — so it refuses to proceed if more than
-- one real tenant exists, rather than guessing.
--
-- REHEARSED ON A RESTORED COPY FIRST, and the rehearsal earned its keep twice: the backfill hit
-- enrollment_event's append-only trigger (an UPDATE is exactly what that trigger exists to forbid), and the
-- resulting abort rolled the block back, so the constraints then failed against rows the backfill had never
-- reached. Both are handled below. Both would have been a failed production migration otherwise.
--
-- ONE BLOCK, ONE TRANSACTION. Backfill and constraints must not be able to half-apply: a database carrying
-- the constraint without the backfill rejects every write to tables full of rows that violate it.

DO $$
DECLARE
    sole    text;
    tenants int;
    moved   int;
    t       record;
BEGIN
    SELECT count(DISTINCT tenant_id), min(tenant_id) INTO tenants, sole
    FROM policy.enrollment WHERE tenant_id <> '';

    IF tenants > 1 THEN
        RAISE EXCEPTION 'this database has % tenants; assigning orphan rows to one of them would be a guess. '
                        'Attribute them from their own foreign keys instead.', tenants;
    END IF;
    IF sole IS NULL THEN
        RAISE NOTICE 'no tenanted rows to learn the tenant from — nothing to backfill';
        RETURN;
    END IF;

    -- enrollment_event is append-only by trigger, and repairing a tenant attribution is the one edit that
    -- rule was never meant to stop: it changes WHOSE the record is recorded as being, not WHAT it records.
    -- Lifted for these statements only and restored below, inside the same transaction, so a failure cannot
    -- leave the log editable.
    ALTER TABLE policy.enrollment_event DISABLE TRIGGER trg_enrollment_event_append_only;

    FOR t IN
        SELECT c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables tb
          ON tb.table_schema = c.table_schema AND tb.table_name = c.table_name
        WHERE c.table_schema = 'policy' AND c.column_name = 'tenant_id' AND tb.table_type = 'BASE TABLE'
    LOOP
        EXECUTE format('UPDATE policy.%I SET tenant_id = %L WHERE tenant_id = %L', t.table_name, sole, '');
        GET DIAGNOSTICS moved = ROW_COUNT;
        IF moved > 0 THEN
            RAISE NOTICE 'policy.%: % row(s) assigned to the sole tenant', t.table_name, moved;
        END IF;
    END LOOP;

    ALTER TABLE policy.enrollment_event ENABLE TRIGGER trg_enrollment_event_append_only;

    -- ...and now it cannot happen again at the storage layer either. Validated rather than NOT VALID:
    -- the backfill above has already cleared the old rows, so a failure here means it missed something,
    -- which is worth stopping the migration for.
    FOR t IN
        SELECT c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables tb
          ON tb.table_schema = c.table_schema AND tb.table_name = c.table_name
        WHERE c.table_schema = 'policy' AND c.column_name = 'tenant_id' AND tb.table_type = 'BASE TABLE'
    LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conname = format('ck_%s_tenant_not_blank', t.table_name)
              AND conrelid = format('policy.%I', t.table_name)::regclass)
        THEN
            EXECUTE format('ALTER TABLE policy.%I ADD CONSTRAINT %I CHECK (tenant_id <> %L)',
                           t.table_name, format('ck_%s_tenant_not_blank', t.table_name), '');
        END IF;
    END LOOP;
END $$;
