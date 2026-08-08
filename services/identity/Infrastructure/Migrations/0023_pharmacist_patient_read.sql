-- identity-service — 0023 the pharmacist may identify the person at the counter. Additive + idempotent.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- A pharmacist hands controlled medication to a human being. Until now the dispensing screen could not name
-- that human being: the queue showed a masked token (•••1df2), the identity strip showed "(name unavailable)"
-- beside a member number, and the only available check on "is this the right patient" was a number the
-- patient had just read out loud. That is not a min-necessary win — it is a dispensing safety gap dressed as
-- one.
--
-- It also broke SEARCH, silently, which is how it stayed hidden. pharmacy-service resolves a card number +
-- member number through patient-service's /beneficiaries/resolve under the CALLER's token. Without
-- patient:read that call answered 403, IBeneficiaryResolver returned null, and the endpoint replied with an
-- empty list — "this member has no prescriptions", about a member who had three. A wrong answer with a 200
-- on it.
--
-- ============================================================================================================
-- WHAT THIS DOES AND DOES NOT DISCLOSE
-- ============================================================================================================
-- The scope opens the beneficiary READ; the field projector still decides what comes back, and pharmacist is
-- granted only the `prescription` field-class on top of the baseline. So a pharmacist receives:
--
--     identity  →  name, member number, card number, date of birth, sex, nationality, status   ← baseline
--     pii       →  STRIPPED (national ID, UNHCR number, passport VALUES)
--     contact   →  STRIPPED (phone, address)
--
-- Exactly "who is in front of me", and none of their documents. This is the same case PatientPolicies.Readers
-- already makes for reception — "identify the caller at the door" — and the read stays tenant-gated and
-- audited as Sensitive, so every one of them is on the PHI record.
--
-- The ≥2-identifier rule on /beneficiaries/resolve is untouched: a card number alone still resolves nobody
-- (doc 43 §7 D5 — a card is a lookup key, not an authenticator).

INSERT INTO identity.role_scope (role_name, scope_name)
VALUES ('pharmacist', 'patient:read')
ON CONFLICT DO NOTHING;

-- Fan out to every provisioned tenant — after 0012 each owns its own grant set and does not inherit live.
INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT DISTINCT rs.tenant_id, 'pharmacist', 'patient:read'
FROM identity.role_scope rs
WHERE rs.tenant_id <> ''
ON CONFLICT DO NOTHING;
