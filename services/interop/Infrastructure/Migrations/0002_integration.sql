-- interop-service — Phase 13.2 integration-readiness layer (adapters + anti-corruption layer). Config + staging
-- ONLY; the core depends on no partner schema (16-service-architecture; ADR-0016). Enablement is DPIA-gated:
-- an integration is Disabled until a DPIA sign-off AND a data-sharing agreement reference exist (20-compliance §6).

-- Partner registry (config only, no PHI).
CREATE TABLE IF NOT EXISTS interop.integration_partner (
    partner_id                  text PRIMARY KEY,
    name                        text NOT NULL,
    direction                   text NOT NULL CHECK (direction IN ('Inbound','Outbound','Bidirectional')),
    transport                   text NOT NULL CHECK (transport IN ('FhirRest','Hl7v2','Rest','Batch','File')),
    status                      text NOT NULL DEFAULT 'Disabled' CHECK (status IN ('Disabled','Enabled')),
    dpia_status                 text NOT NULL DEFAULT 'NotStarted' CHECK (dpia_status IN ('NotStarted','InProgress','SignedOff')),
    data_sharing_agreement_ref  text,
    cross_border                boolean NOT NULL DEFAULT false,
    updated_at                  timestamptz NOT NULL DEFAULT now(),
    -- Belt-and-suspenders at the DB level: a row can only be Enabled with BOTH artifacts present. The DpiaGate
    -- enforces this at runtime + CI; this CHECK makes an out-of-band UPDATE that enables without them impossible.
    CONSTRAINT ck_partner_dpia_gate CHECK (
        status = 'Disabled'
        OR (dpia_status = 'SignedOff' AND data_sharing_agreement_ref IS NOT NULL AND length(trim(data_sharing_agreement_ref)) > 0)
    )
);

-- Inbound quarantine/staging — a message lands here, the ACL maps it (→ internal domain events) or it stays
-- quarantined. NEVER promoted to a core table directly.
CREATE TABLE IF NOT EXISTS interop.inbound_staging (
    staging_id   uuid PRIMARY KEY,
    partner_id   text NOT NULL,
    format       text NOT NULL,
    body         text NOT NULL,
    state        text NOT NULL CHECK (state IN ('Mapped','Quarantined')),
    reason       text,
    received_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_inbound_staging_partner ON interop.inbound_staging (partner_id, state);

-- Seed the roadmap partners as DISABLED / DPIA-pending (35 §10). They advertise the extension points; each stays
-- off until a DPIA + data-sharing agreement are recorded and the DpiaGate passes. Idempotent.
INSERT INTO interop.integration_partner (partner_id, name, direction, transport, status, dpia_status, cross_border) VALUES
    ('digital-referral-network', 'Digital Referral Network (FHIR)', 'Bidirectional', 'FhirRest', 'Disabled', 'NotStarted', false),
    ('hl7v2-referral',           'Digital Referral Network (HL7 v2)', 'Bidirectional', 'Hl7v2', 'Disabled', 'NotStarted', false),
    ('unhcr-identity',           'UNHCR Identifier Validation', 'Bidirectional', 'Batch', 'Disabled', 'NotStarted', true),
    ('government-claims',        'Government Claims/Eligibility', 'Bidirectional', 'Rest', 'Disabled', 'NotStarted', false),
    ('insurer-eligibility',      'Insurer Claims/Eligibility', 'Bidirectional', 'Rest', 'Disabled', 'NotStarted', false)
ON CONFLICT (partner_id) DO NOTHING;

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE ON interop.integration_partner TO hbmp_app;
        GRANT SELECT, INSERT, UPDATE ON interop.inbound_staging TO hbmp_app;
    END IF;
END $$;
