-- provider-service — 0012 BACKFILL: provider_type / service_type 'Imaging' → 'Radiology'.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- 29.1 / design 45 §1 — the BACKFILL step, after 0011 expanded both CHECKs.
--
-- The two columns are rewritten in ONE migration on purpose. `provider.provider_type` says what kind of
-- organisation this is; `contract_service_line.service_type` says what a contract line is priced for, and
-- pricing joins them by string. Splitting the rewrite across two deploys would leave a window in which a
-- radiology centre's contract lines are typed 'Imaging' while the centre is typed 'Radiology' — the join
-- returns nothing, and an order that finds no priced service line does not fail loudly, it prices at zero.
--
-- provider_user.role IS rewritten in place, unlike the identity grants, because the table's shape forbids the
-- additive form: `user_id` is the PRIMARY KEY, so a provider-bound account holds exactly ONE role and there is
-- no second row to add. That is safe here only because the binding is not what authorises — the token's roles
-- are, and libs/auth/LegacyRoleAliases expands those to both spellings for the whole window, while
-- ProviderAccessGuard's provider-scoped role list accepts both. Were the binding the authority, this column
-- would need a widening migration first; it is not, and the tests in ProviderRulesTests hold that line.

UPDATE provider.provider
SET provider_type = 'Radiology'
WHERE provider_type = 'Imaging';

UPDATE provider.contract_service_line
SET service_type = 'Radiology'
WHERE service_type = 'Imaging';

-- Provider-bound accounts: one row per user (user_id is the PK), so this is an in-place rewrite.
UPDATE provider.provider_user
SET role = 'radiology_tech'
WHERE role = 'imaging_tech';

DO $$
DECLARE remaining int;
BEGIN
    SELECT (SELECT count(*) FROM provider.provider WHERE provider_type = 'Imaging')
         + (SELECT count(*) FROM provider.contract_service_line WHERE service_type = 'Imaging')
         + (SELECT count(*) FROM provider.provider_user WHERE role = 'imaging_tech')
      INTO remaining;
    IF remaining > 0 THEN
        RAISE EXCEPTION '% provider row(s) still typed Imaging', remaining;
    END IF;
END $$;
