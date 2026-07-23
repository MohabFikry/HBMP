-- patient-service — 0002 registration application aggregate (1.4, US-003).
-- Distinct from beneficiary.status: beneficiary stays Pending until activation.
CREATE TABLE IF NOT EXISTS patient.registration (
    registration_id   uuid PRIMARY KEY,
    beneficiary_id    uuid NOT NULL REFERENCES patient.beneficiary(beneficiary_id),
    status            text NOT NULL DEFAULT 'Pending'
                      CHECK (status IN ('Pending','InfoRequested','Rejected','Active')),
    documents_verified boolean NOT NULL DEFAULT false,
    coverage_bound     boolean NOT NULL DEFAULT false,
    notes             text,
    row_version       int NOT NULL DEFAULT 0,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_registration_beneficiary ON patient.registration (beneficiary_id);
