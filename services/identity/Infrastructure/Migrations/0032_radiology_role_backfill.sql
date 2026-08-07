-- identity-service — 0032 BACKFILL: move every `imaging_tech` GRANT onto `radiology_tech`.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- 29.1 / design 45 §1 — the BACKFILL step. 0031 created the role and copied its scopes; this moves the people.
-- After this migration every human who could act as an imaging technician can act as a radiology technician.
--
-- GRANTS ARE ADDED, NOT MOVED. The old grant stays until the contract step, and that is the whole design:
--   * a session already open keeps a token naming `imaging_tech` for up to 300 s (token-contract.md §4), and
--     revoking the grant underneath it turns a valid token into a 403 mid-procedure;
--   * services deploy independently, so a service still checking the old name must still find it granted;
--   * and a backfill that REMOVES is not reversible by re-running it. If the switch has to be rolled back,
--     an additive backfill needs no compensating migration — the old grant never went away.
-- The contract migration removes the `imaging_tech` grants, once, when nothing can still be holding one.
--
-- Both `user_role` (the ASP.NET Core Identity table) and `membership_role` (the phase-21 tenant membership,
-- which is the actual security principal — token-contract.md §2) carry role grants. Backfilling one and not
-- the other would leave a technician who authenticates fine and then resolves to no membership role, which
-- presents as an account with no portal rather than as an error.

-- ---- ASP.NET Core Identity user_role -----------------------------------------------------------------------

INSERT INTO identity.user_role (user_id, role_id)
SELECT ur.user_id, new_role.id
FROM identity.user_role ur
JOIN identity.role old_role ON old_role.id = ur.role_id AND old_role.name = 'imaging_tech'
CROSS JOIN (SELECT id FROM identity.role WHERE name = 'radiology_tech') new_role
ON CONFLICT DO NOTHING;

-- ---- Phase-21 tenant membership_role -----------------------------------------------------------------------

INSERT INTO identity.membership_role (membership_id, role_id, granted_by)
SELECT mr.membership_id, new_role.id, 'migration:0032'
FROM identity.membership_role mr
JOIN identity.role old_role ON old_role.id = mr.role_id AND old_role.name = 'imaging_tech'
CROSS JOIN (SELECT id FROM identity.role WHERE name = 'radiology_tech') new_role
ON CONFLICT DO NOTHING;

-- ---- Assert nobody was left behind -------------------------------------------------------------------------
-- The failure this catches is silent by nature: a technician who cannot reach their queue after the switch
-- looks like an access-request ticket, not like a migration that half-ran.

DO $$
DECLARE stranded int;
BEGIN
    SELECT count(*) INTO stranded
    FROM identity.membership_role mr
    JOIN identity.role r ON r.id = mr.role_id AND r.name = 'imaging_tech'
    WHERE NOT EXISTS (
        SELECT 1 FROM identity.membership_role mr2
        JOIN identity.role r2 ON r2.id = mr2.role_id AND r2.name = 'radiology_tech'
        WHERE mr2.membership_id = mr.membership_id);
    IF stranded > 0 THEN
        RAISE EXCEPTION '% membership(s) still hold imaging_tech without radiology_tech', stranded;
    END IF;
END $$;
