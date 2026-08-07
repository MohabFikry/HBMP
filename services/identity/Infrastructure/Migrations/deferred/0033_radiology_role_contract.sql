-- identity-service — 0033 CONTRACT: drop `imaging_tech`. DEFERRED — see the banner below.
--
-- ============================================================================================================
-- ⚠ THIS MIGRATION IS NOT APPLIED BY tools/ci/apply-migrations.sh.
-- ============================================================================================================
-- It lives in Migrations/deferred/ deliberately. The runner globs `Migrations/*.sql` at maxdepth 1, so a file
-- here is committed, reviewed and version-controlled but does NOT ship with the deploy that renames anything.
--
-- That structure IS the dual-accept window. Had this file sat beside 0032, it would have been applied in the
-- same breath as the expand and the backfill — the window would have been zero seconds wide, and every access
-- token in flight at that moment would have been holding a role that no longer existed. The whole point of
-- expand → backfill → switch → contract is that the last step happens LATER, and "later" has to be enforced by
-- something other than an intention.
--
-- APPLY IT ONLY WHEN BOTH ARE TRUE (docs/runbooks/radiology-rename.md):
--   1. Longer than the maximum access-token TTL has elapsed since the SWITCH deploy — 300 s
--      (docs/security/token-contract.md §4). No token naming `imaging_tech` can still be inside its validity.
--   2. The outbox has fully drained of pre-switch events, verified — not assumed — by the outbox depth being
--      zero and the oldest undelivered message being newer than the switch.
--
-- APPLY IT WITH:  tools/ci/apply-deferred-migrations.sh
--
-- AND IN THE SAME CHANGE, remove the code-side dual-accept, or this migration makes things WORSE rather than
-- better — a half-contracted rename is the one state with no working spelling:
--   * libs/auth/LegacyRoleAliases.cs           — empty the Aliases table (WindowOpen goes false)
--   * libs/auth/Tests/LegacyRoleAliasTests.cs  — delete; its canary asserts the window is open
--   * services/identity/Domain/IdentityContract.cs — drop "imaging_tech" (count returns to 21)
--   * services/admin/Domain/RoleCatalog.cs     — drop the imaging_tech tier row
--   * libs/authz/*                             — drop "imaging_tech" from every role set
--   * apps/web/src/config.ts                   — drop the ["imaging_tech", "radiology"] ROLE_MAP row
--   * services/provider/{Api/ProviderAccessGuard.cs,Domain/ProviderUserRules.cs}
--
-- NOT INCLUDED, AND NEVER WILL BE: the audit trail. Historical audit_event rows say `imaging_tech` and are
-- hash-chained; rewriting them is the tampering the chain exists to detect. They are resolved for READERS by
-- services/audit/Domain/LegacyIdentifierDisplay.cs, which is permanent (design 45 §1 (c)).

BEGIN;

-- Withdraw the legacy grants first, while the role still exists to be joined against.
DELETE FROM identity.membership_role mr
USING identity.role r
WHERE r.id = mr.role_id AND r.name = 'imaging_tech';

DELETE FROM identity.user_role ur
USING identity.role r
WHERE r.id = ur.role_id AND r.name = 'imaging_tech';

DELETE FROM identity.role_scope WHERE role_name = 'imaging_tech';

-- Refuse to drop the role if anyone would lose access by it. The backfill (0032) should have given every
-- imaging_tech membership a radiology_tech one; if it did not, stop here rather than de-provision a
-- technician mid-shift.
DO $$
DECLARE orphans int;
BEGIN
    SELECT count(*) INTO orphans
    FROM identity.membership_role mr
    JOIN identity.role r ON r.id = mr.role_id
    WHERE r.name = 'radiology_tech';
    IF orphans = 0 AND EXISTS (SELECT 1 FROM identity.role WHERE name = 'radiology_tech') THEN
        RAISE WARNING 'no membership holds radiology_tech — verify 0032 ran before contracting';
    END IF;
END $$;

DELETE FROM identity.role WHERE name = 'imaging_tech';

COMMIT;
