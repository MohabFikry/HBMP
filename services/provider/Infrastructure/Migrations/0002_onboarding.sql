-- provider-service — 0002 onboarding: provider-scoped user accounts (FR-NET-003, FR-IAM-002/010).
-- A provider_user is stamped with exactly one provider_id and may only hold provider-scoped roles for
-- THAT provider. Suspending/terminating the provider revokes every one of its users (status → Revoked).

CREATE TABLE IF NOT EXISTS provider.provider_user (
    user_id     uuid PRIMARY KEY,
    provider_id uuid NOT NULL REFERENCES provider.provider(provider_id),
    tenant_id   text NOT NULL,
    subject_ref text NOT NULL,
    role        varchar(32) NOT NULL CHECK (role IN ('provider_admin','lab_tech','imaging_tech','pharmacist')),
    status      varchar(16) NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Revoked')),
    created_at  timestamptz NOT NULL DEFAULT now(),
    revoked_at  timestamptz,
    CONSTRAINT uq_provider_user_subject UNIQUE (tenant_id, subject_ref)
);
CREATE INDEX IF NOT EXISTS ix_provider_user_provider ON provider.provider_user (provider_id);
