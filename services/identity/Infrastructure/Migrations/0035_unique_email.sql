-- identity-service — 0035: an email address identifies exactly one account.
--
-- ============================================================================================================
-- WHY THIS IS NOW A CONSTRAINT AND NOT A PREFERENCE
-- ============================================================================================================
-- 28.8 made the email address a SIGN-IN CREDENTIAL: `SessionApiEndpoints.ResolveLoginAsync` looks an account
-- up by address before falling back to the username. While the address was merely a contact field, two staff
-- sharing a departmental mailbox was untidy. Now it decides WHOSE PASSWORD IS CHECKED — `FindByEmailAsync`
-- would return one of them, and which one it returned would be an accident of row order.
--
-- ASP.NET Identity's own `RequireUniqueEmail` (turned on in the same change) is a read-then-write with a gap
-- in the middle: two administrators creating the same address at the same moment both read "not taken" and
-- both write. Only the database can actually make this true, which is what this index is for. The
-- application check stays, because it produces a 409 with a sentence in it rather than a constraint
-- violation.
--
-- ============================================================================================================
-- EXPAND/CONTRACT
-- ============================================================================================================
-- This is the EXPAND half and it is deliberately additive: the index is created, and nothing is made NOT
-- NULL. Accounts predating 28.8 may legitimately have no address at all — service accounts, and the seeded
-- fixtures — and a NOT NULL here would fail the migration on a populated database rather than surface those
-- accounts for somebody to decide about. The partial index below permits any number of NULLs and forbids a
-- second copy of any actual address, which is exactly the rule.
--
-- The API requires an address on CREATE, so the set of address-less accounts can only shrink from here.

-- Duplicates, if any exist, must be resolved before the index can be built. This reports them loudly rather
-- than letting the CREATE INDEX fail with a message that names one row and not the conflict.
DO $$
DECLARE dupes text;
BEGIN
    SELECT string_agg(normalized_email, ', ')
      INTO dupes
      FROM (
        SELECT normalized_email
          FROM identity."user"
         WHERE normalized_email IS NOT NULL
         GROUP BY normalized_email
        HAVING count(*) > 1
      ) d;

    IF dupes IS NOT NULL THEN
        RAISE EXCEPTION
            'Cannot enforce unique email: these addresses are held by more than one account: %. '
            'Resolve them (deprovision the duplicate, or give it its own address) and re-run.', dupes;
    END IF;
END $$;

-- Partial, so the address-less accounts described above are all permitted and none of them collide with each
-- other. `normalized_email` and not `email`: Identity matches on the normalized column, so uniqueness has to
-- be asserted on the same one it looks up by, or "Ali@x.org" and "ali@x.org" would be two accounts that
-- resolve to one.
CREATE UNIQUE INDEX IF NOT EXISTS ux_user_normalized_email
    ON identity."user" (normalized_email)
    WHERE normalized_email IS NOT NULL;
