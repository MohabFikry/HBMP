-- 29.2 (design 45 §2, invariant 3) — the CPT code a referral was raised FOR.
--
-- An E/M code creates a REFERRAL rather than a Procedure order, "carrying the CPT code as the requested
-- service". Without the code the referral names only a specialty, and a referral that names no service is
-- the open-ended one nobody can close: loop closure is closure AGAINST something, and "cardiology opinion"
-- is not something a report can be matched to.
--
-- ADDITIVE AND NULLABLE, deliberately. Referrals predate this phase and are raised from paths that carry no
-- CPT code; a NOT NULL column with a backfilled placeholder would invent a requested service for every
-- historical referral and make "was this raised for a specific service?" unanswerable. NULL means NOT
-- RECORDED — never "no service".

ALTER TABLE pharmacy.referral
    ADD COLUMN IF NOT EXISTS requested_service_code        varchar(16) NULL,
    ADD COLUMN IF NOT EXISTS requested_service_code_system varchar(16) NULL;

COMMENT ON COLUMN pharmacy.referral.requested_service_code IS
    '29.2 — the CPT code this referral was raised for (design 45 §2). NULL = not recorded, which is not the '
    'same as no service: referrals raised before this column existed, and those raised from paths with no '
    'code, are legitimately null.';

COMMENT ON COLUMN pharmacy.referral.requested_service_code_system IS
    'The coding system of requested_service_code — CPT today. Named rather than assumed, because a bare '
    'code with no system is the thing that quietly becomes ambiguous the first time a second system arrives.';

-- The ordering doctor's open-loop worklist filters on status and reads this alongside it.
CREATE INDEX IF NOT EXISTS ix_referral_requested_service
    ON pharmacy.referral (requested_service_code)
    WHERE requested_service_code IS NOT NULL;
