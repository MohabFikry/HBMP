-- provider-service — 0013 CONTRACT: provider_type / service_type / provider_user.role drop 'Imaging'.
--
-- ⚠ NOT applied by tools/ci/apply-migrations.sh. See
-- services/identity/Infrastructure/Migrations/deferred/0033_radiology_role_contract.sql and
-- docs/runbooks/radiology-rename.md.
--
-- Apply with: tools/ci/apply-deferred-migrations.sh

BEGIN;

UPDATE provider.provider            SET provider_type = 'Radiology'  WHERE provider_type = 'Imaging';
UPDATE provider.contract_service_line SET service_type = 'Radiology' WHERE service_type = 'Imaging';
UPDATE provider.provider_user       SET role = 'radiology_tech'      WHERE role = 'imaging_tech';

ALTER TABLE provider.provider DROP CONSTRAINT IF EXISTS ck_provider_provider_type;
ALTER TABLE provider.provider
    ADD CONSTRAINT ck_provider_provider_type
    CHECK (provider_type IN ('Hospital','Clinic','Lab','Pharmacy','Radiology'));

ALTER TABLE provider.contract_service_line DROP CONSTRAINT IF EXISTS ck_contract_service_line_service_type;
ALTER TABLE provider.contract_service_line
    ADD CONSTRAINT ck_contract_service_line_service_type
    CHECK (service_type IN ('Lab','Radiology','Consult','Procedure'));

ALTER TABLE provider.provider_user DROP CONSTRAINT IF EXISTS ck_provider_user_role;
ALTER TABLE provider.provider_user
    ADD CONSTRAINT ck_provider_user_role
    CHECK (role IN ('provider_admin','lab_tech','radiology_tech','pharmacist'));

COMMIT;
