-- provider-service — 0011 EXPAND: provider_type and service_type accept 'Radiology' as well as 'Imaging'.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- 29.1 / design 45 §1 — the EXPAND step for the provider vocabulary. 0012 backfills the rows; only the
-- deferred contract migration narrows the CHECKs back down.
--
-- TWO columns carry the value, not one:
--   provider.provider.provider_type            — what KIND of organisation this is
--   provider.contract_service_line.service_type — what a contract line is priced FOR
-- A rename that moved only the first would leave every radiology contract line priced under a value the
-- provider itself no longer claims, and the join that reconciles them is by string.
--
-- provider_user.role gains 'radiology_tech' in the same breath, because a provider-bound account IS how a
-- technician reaches their queue: expanding the role in identity (0031) without expanding it here would let
-- the token name a role that the provider binding then refuses to store.

ALTER TABLE provider.provider DROP CONSTRAINT IF EXISTS provider_provider_type_check;
ALTER TABLE provider.provider DROP CONSTRAINT IF EXISTS ck_provider_provider_type;
ALTER TABLE provider.provider
    ADD CONSTRAINT ck_provider_provider_type
    CHECK (provider_type IN ('Hospital','Clinic','Lab','Pharmacy','Imaging','Radiology'));

ALTER TABLE provider.contract_service_line DROP CONSTRAINT IF EXISTS contract_service_line_service_type_check;
ALTER TABLE provider.contract_service_line DROP CONSTRAINT IF EXISTS ck_contract_service_line_service_type;
ALTER TABLE provider.contract_service_line
    ADD CONSTRAINT ck_contract_service_line_service_type
    CHECK (service_type IN ('Lab','Imaging','Radiology','Consult','Procedure'));

ALTER TABLE provider.provider_user DROP CONSTRAINT IF EXISTS provider_user_role_check;
ALTER TABLE provider.provider_user DROP CONSTRAINT IF EXISTS ck_provider_user_role;
ALTER TABLE provider.provider_user
    ADD CONSTRAINT ck_provider_user_role
    CHECK (role IN ('provider_admin','lab_tech','imaging_tech','radiology_tech','pharmacist'));
