-- policy-service — 0017 the member's own tier and contribution.
--
-- Cost share has until now been a property of the PLAN VERSION: one cost-share row per benefit category per
-- network tier, which is right for "what does this plan pay at this tier" and cannot answer "what does THIS
-- member pay". Mersal's intake sheet carries both per person — two members on one plan routinely sit on
-- different tiers and carry different shares, which is exactly the case a plan-level matrix cannot express.
--
-- These are OVERRIDES, not a replacement. Null means "take the plan's answer", which is what every existing
-- row means and why no backfill is needed. Only a value that is actually present overrides, so a deployment
-- that never sets them behaves exactly as it does today.

ALTER TABLE policy.enrollment
    ADD COLUMN IF NOT EXISTS network_tier_id uuid;

ALTER TABLE policy.enrollment
    ADD COLUMN IF NOT EXISTS contribution_percent numeric(5,2);

-- Bounded at the datastore as well as in the domain: a contribution outside 0..100 silently inverts every
-- cost-share sum that reads it, and a bulk import is precisely the path that would carry one in unnoticed.
DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_enrollment_contribution_range'
    ) THEN
        ALTER TABLE policy.enrollment
            ADD CONSTRAINT ck_enrollment_contribution_range
            CHECK (contribution_percent IS NULL
                   OR (contribution_percent >= 0 AND contribution_percent <= 100));
    END IF;
END $$;

-- "Which members sit on the restricted network" is a question the network team asks constantly and could
-- previously only be answered by walking every plan version.
CREATE INDEX IF NOT EXISTS ix_enrollment_network_tier ON policy.enrollment (network_tier_id)
    WHERE network_tier_id IS NOT NULL;
