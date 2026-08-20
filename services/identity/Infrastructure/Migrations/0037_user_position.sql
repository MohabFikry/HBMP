-- identity-service — 0037: the person's JOB TITLE, alongside their display name.
--
-- WHAT IT IS, AND WHAT IT IS NOT
-- ============================================================================================================
-- `position` is what the organisation calls the job — "Senior Pharmacist", "Head of Reception". It is NOT a
-- role: a role decides what the platform lets the account do and comes from a frozen vocabulary; this is a
-- caption somebody types. An account can be a "Senior Pharmacist" holding the `reception` role, and every
-- authorization decision must keep answering to the role. Nothing reads this column to decide anything.
--
-- It lives on `user` rather than on `tenant_membership` because the requirement is that it reads the same
-- whichever portal the person is working in, and a membership-scoped title would differ between two of them
-- by construction.
--
-- EXPAND-PHASE ONLY: nullable, no default, no backfill. An older replica that does not know the column keeps
-- inserting and updating users exactly as before; a newer one writes it when an administrator supplies one.
-- There is no contract step to follow, because a job title nobody has recorded is a legitimate state — the
-- app bar falls back to the portal's own label — so this column never becomes NOT NULL.
--
-- Additive and idempotent.

ALTER TABLE identity."user" ADD COLUMN IF NOT EXISTS position varchar(120);

COMMENT ON COLUMN identity."user".position IS
    'Job title as the organisation states it. Display only — never an authorization input (see 0037 header).';
