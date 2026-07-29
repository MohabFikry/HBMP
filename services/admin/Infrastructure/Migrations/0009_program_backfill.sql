-- admin-service — 0009: backfill the programme switches for tenants that already exist
-- (phase 21.4 propagation, design 40 §4).
--
-- 0008 created admin.tenant_feature and correctly made ABSENCE MEAN DISABLED: a programme nobody has switched
-- on has not been switched on, and the other default would have enabled every module for every tenant the
-- moment that table shipped empty.
--
-- That default is right for a NEW tenant and wrong for the ones already running. Every module currently works
-- for them — nothing has ever consulted these switches — so leaving the table empty and then wiring the gate
-- would take every existing organisation off every module simultaneously. To the partner NGO on the other end
-- that is not a policy change, it is an outage, and one we would have caused by tidying up.
--
-- So: existing tenants are recorded as ON, which is a statement of what is already true rather than a new
-- grant. Identity-service migration 0015 states the same fact in its projection from its own tenant list, so
-- the two sides agree at t0 and TenantFeatureChanged keeps them in step from then on.
--
-- ON CONFLICT DO NOTHING, not DO UPDATE: a row that already exists was put there by a real administrative
-- decision — possibly a deliberate "off" — and a backfill must never overrule one.
--
-- Expand-phase: inserts only, no schema change.

INSERT INTO admin.tenant_feature (tenant_id, feature_key, enabled, changed_by, changed_at)
SELECT t.tenant_id, f.feature_key, true, 'migration:0009_program_backfill', now()
FROM (
    -- Every tenant this schema knows about. role_binding is admin-service's own record of who operates in
    -- which tenant, so this needs no cross-schema read and no list maintained by hand.
    SELECT DISTINCT tenant_id FROM admin.role_binding
    UNION
    SELECT DISTINCT tenant_id FROM admin.tenant_limit
    UNION
    SELECT DISTINCT tenant_id FROM admin.tenant_feature
) t
CROSS JOIN (VALUES
    ('claims'), ('callcentre'), ('interop'), ('reporting_extracts'), ('pharmacy'),
    ('orders'), ('approvals'), ('emr'), ('finance'), ('documents'), ('case_management')
) AS f(feature_key)
ON CONFLICT (tenant_id, feature_key) DO NOTHING;
