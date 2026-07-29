-- identity-service — 0015: the programme-enablement projection (phase 21.4 propagation, design 40 §4/§5).
--
-- WHY THE ISSUER NEEDS A COPY. The switches are administered in admin-service (admin.tenant_feature) but they
-- are ENFORCED wherever the module lives, off the `features` claim — design 40 §5 mode 1: "enabled features
-- are resolved once at token issuance and carried in the signed token", so every later check is an in-memory
-- lookup instead of a network call on the hot path. The issuer cannot read admin.tenant_feature: that is
-- another service's schema, and reaching across would make one deployable depend on another's table shape.
-- So the change travels as a TenantFeatureChanged event and this table is the issuer's local answer to
-- "what is switched on for this tenant".
--
-- It is a PROJECTION, never a second source of truth: nothing in identity-service writes it except the
-- consumer, and administering a switch here would produce a tenant whose token disagrees with its own
-- administration screen.
--
-- Expand-phase only: two new tables, both additive.

CREATE TABLE IF NOT EXISTS identity.tenant_feature (
    tenant_id   text        NOT NULL,
    feature_key varchar(32) NOT NULL,
    enabled     boolean     NOT NULL DEFAULT false,
    -- The moment the CHANGE was made, as stamped by admin-service — not the moment we received it. Delivery
    -- is at-least-once and unordered, so this is what lets the consumer refuse to move a row backwards.
    changed_at  timestamptz NOT NULL DEFAULT now(),
    -- Which event last moved this row. Diagnostic: "why does this tenant have claims off" is answerable
    -- without correlating timestamps across two services by eye.
    source_event_id uuid,
    PRIMARY KEY (tenant_id, feature_key)
);

COMMENT ON TABLE identity.tenant_feature IS
    'Projection of admin.tenant_feature, fed by TenantFeatureChanged. Read at token issuance to build the '
    '`features` claim. Absence of a row means DISABLED (design 40 §4).';

-- At-least-once delivery means the same event arrives twice. Dedupe is durable rather than in-memory because
-- the consumer restarts and an in-memory set forgets everything it had seen — which for this projection is
-- survivable (the apply is idempotent) but for the shared IProcessedEventStore contract is not.
CREATE TABLE IF NOT EXISTS identity.processed_event (
    event_id     uuid        PRIMARY KEY,
    event_type   text,
    processed_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_processed_event_at ON identity.processed_event (processed_at DESC);

GRANT SELECT, INSERT, UPDATE, DELETE ON identity.tenant_feature  TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON identity.processed_event TO hbmp_app;

-- ---------------------------------------------------------------------------------------------------------
-- BACKFILL — the part that decides whether this change is safe to deploy.
--
-- Absence of a row means DISABLED. That is the right default for a NEW tenant: a programme nobody has
-- switched on has not been switched on. It is the WRONG default for the tenants already running, because
-- every module currently works for them — so shipping an empty table and then wiring the gate would take
-- every existing organisation off every module at once, and read to them as an outage we caused.
--
-- So existing tenants are backfilled ON: that is what "no change in behaviour" means here. The same rule is
-- applied to the source of truth in admin-service migration 0009, from that schema's own tenant list, so the
-- two agree at t0 and the event stream keeps them in step afterwards. Nothing derived, nothing inferred —
-- both sides state the same fact about the same instant.
--
-- Deliberately NOT ON CONFLICT DO UPDATE: if a row already exists it was put there by a real administrative
-- decision, and a backfill must never overrule one.
INSERT INTO identity.tenant_feature (tenant_id, feature_key, enabled, changed_at)
SELECT t.tenant_id, f.feature_key, true, now()
FROM (SELECT DISTINCT tenant_id FROM identity.tenant_membership WHERE NOT is_deleted) t
CROSS JOIN (VALUES
    ('claims'), ('callcentre'), ('interop'), ('reporting_extracts'), ('pharmacy'),
    ('orders'), ('approvals'), ('emr'), ('finance'), ('documents'), ('case_management')
) AS f(feature_key)
ON CONFLICT (tenant_id, feature_key) DO NOTHING;
