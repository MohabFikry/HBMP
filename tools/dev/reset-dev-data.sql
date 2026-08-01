-- ============================================================================================================
-- reset-dev-data.sql — wipe the OPERATIONAL data from a local dev database, keep everything you need to log in
-- and everything that is reference rather than business data.
--
--   psql -h localhost -p 55432 -U hbmp -d hbmp -v ON_ERROR_STOP=1 -f tools/dev/reset-dev-data.sql
--
-- DEV ONLY. It refuses to run against a database whose name is not `hbmp` (see the guard below), but that is a
-- speed bump, not a safety system: this truncates ~40 tables and there is no undo. Take a dump first —
-- `pg_dump -Fc` — and keep it until you are satisfied with what replaced the data.
--
-- WHAT IS KEPT, AND WHY
--
--   identity.*     Your logins. 21 users with roles, sessions and tokens. Wiping them means the portal cannot
--                  be opened at all until identity-service restarts and re-seeds (which it will, in
--                  Development, from IDENTITY_DEMO_PASSWORD) — an unnecessary risk for a data reset.
--   masterdata.*   ICD / CPT / ATC / drug reference. ~52,000 rows loaded from `Raw Files/` by the
--                  masterdata-loader. Reloadable, but slow, and nothing about it is "wrong test data".
--   admin.*        Tenant configuration, feature flags, role bindings. Configuration, not business data.
--   audit.*        Append-only and hash-chained (19-audit-strategy). It is a record of what the platform did,
--                  and that stays true after its subjects are deleted. Truncating it is a separate, deliberate
--                  decision — do it by hand if you want a clean chain.
--   *.\*_seq        Business-key sequences (CALL-, ORD-, RX-, ENC-…). RESET rather than dropped, below.
--   policy.benefit_category, provider.specialty, notification.notification_template
--                  Reference rows the seed and the services look up by code. Regenerating them would only
--                  change their ids for no benefit.
--
-- Everything else in the business schemas goes.
-- ============================================================================================================

\set ON_ERROR_STOP on

DO $guard$
BEGIN
    IF current_database() <> 'hbmp' THEN
        RAISE EXCEPTION
            'reset-dev-data.sql refuses to run against database "%". It is written for the local dev DB (hbmp).',
            current_database();
    END IF;
END
$guard$;

-- RLS is FORCED on most business tables, so even the owner is filtered by `app.tenant_id`. TRUNCATE is not
-- row-filtered, but the DELETEs and the verification SELECT at the end are — set the GUC once for the session.
SET app.tenant_id = '11111111-1111-1111-1111-111111111111';

DO $reset$
DECLARE
    -- The schemas whose contents are BUSINESS data.
    business_schemas text[] := ARRAY[
        'patient', 'policy', 'provider', 'eligibility', 'emr', 'callcentre', 'claims', 'orders',
        'pharmacy', 'approvals', 'case', 'finance', 'reporting', 'notification', 'inventory',
        'interop', 'document', 'migration'
    ];
    -- Reference rows inside those schemas that the services resolve by code. Keeping them keeps their ids
    -- stable, which is what lets the seed reference them by code instead of inventing new ones.
    keep_tables text[] := ARRAY[
        'policy.benefit_category',
        'provider.specialty',
        'notification.notification_template'
    ];
    victim text;
    victims text[] := '{}';
    seq_table text;
BEGIN
    SELECT array_agg(format('%I.%I', n.nspname, c.relname) ORDER BY n.nspname, c.relname)
    INTO victims
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE c.relkind = 'r'
      AND n.nspname = ANY (business_schemas)
      AND format('%s.%s', n.nspname, c.relname) <> ALL (keep_tables)
      -- Business-key counters are reset below rather than dropped: the tables have a fixed shape (year,
      -- last_value) that the issuers UPSERT into, and truncating them is fine, but resetting reads clearer.
      AND c.relname NOT LIKE '%\_seq';

    IF victims IS NULL OR cardinality(victims) = 0 THEN
        RAISE EXCEPTION 'found no tables to truncate — the schema list is wrong, refusing to report success';
    END IF;

    RAISE NOTICE 'truncating % table(s) across % business schema(s)…',
        cardinality(victims), cardinality(business_schemas);

    -- ONE statement, CASCADE, RESTART IDENTITY. One statement so foreign keys between these tables never see
    -- a half-empty graph; CASCADE so a table we kept out of the list by mistake fails loudly here rather than
    -- leaving orphans behind.
    EXECUTE format('TRUNCATE TABLE %s RESTART IDENTITY CASCADE', array_to_string(victims, ', '));

    -- Business-key counters back to zero, so the first seeded call is CALL-2026-000001 rather than continuing
    -- from wherever the discarded data left off. A reset environment whose keys start at 000064 reads as if
    -- something was deleted, which is exactly the confusion this script exists to remove.
    FOR seq_table IN
        SELECT format('%I.%I', n.nspname, c.relname)
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind = 'r' AND n.nspname = ANY (business_schemas) AND c.relname LIKE '%\_seq'
    LOOP
        EXECUTE format('DELETE FROM %s', seq_table);
        RAISE NOTICE '  reset %', seq_table;
    END LOOP;
END
$reset$;

-- What survived, so the operator can see the keep-list held rather than take it on trust.
SELECT 'identity.user'      AS kept, count(*) FROM identity."user"
UNION ALL SELECT 'masterdata.atc_class',            count(*) FROM masterdata.atc_class
UNION ALL SELECT 'admin.tenant_feature',            count(*) FROM admin.tenant_feature
UNION ALL SELECT 'policy.benefit_category',         count(*) FROM policy.benefit_category
UNION ALL SELECT 'provider.specialty',              count(*) FROM provider.specialty
UNION ALL SELECT 'notification.notification_template', count(*) FROM notification.notification_template
UNION ALL SELECT '--- wiped ---',                   NULL
UNION ALL SELECT 'patient.beneficiary',             count(*) FROM patient.beneficiary
UNION ALL SELECT 'policy.policy',                   count(*) FROM policy.policy
UNION ALL SELECT 'emr.appointment',                 count(*) FROM emr.appointment
UNION ALL SELECT 'callcentre.call_interaction',     count(*) FROM callcentre.call_interaction;
