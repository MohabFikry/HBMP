-- identity-service — 0025: the pharmacist may ask what the member pays. Additive + idempotent.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- The dispensing counter is where a beneficiary is told what to hand over. Until now the screen could not
-- say: pharmacy-service had no route to a cost share, so the pharmacist quoted from a paper tariff or did not
-- quote at all. For a refugee family deciding whether they can afford a course of antibiotics, "I don't know"
-- and a wrong number are both answers with consequences, and there is no reviewer between the counter and the
-- patient to catch either.
--
-- The scope is `eligibility:check` — the EXISTING scope for exactly this question — rather than a new one.
-- "What does this member pay for this benefit category at this provider" is an eligibility check whoever is
-- asking; minting a second scope for the same question would leave two grants to reason about and two places
-- to revoke.
--
-- ============================================================================================================
-- WHY NOT policy:read, AND WHY NOT A SERVICE ACCOUNT
-- ============================================================================================================
-- `policy:read` is the benefit PRODUCT — every plan, rule and tier on the platform. A pharmacist needs one
-- number about one member, so granting the catalogue would be sizing the scope to the implementation rather
-- than to the need, which is the mistake `practitioner:read` was split out of `provider:read` to avoid.
--
-- Having pharmacy-service fetch it under a service account is forbidden platform-wide (see the note on
-- practitioner:read in IdentityContract): a service-account read is an unattributable read, and this one
-- touches a member's coverage. Forwarding the pharmacist's own token keeps the audit trail pointing at the
-- person who asked.
--
-- ============================================================================================================
-- WHAT THIS DISCLOSES
-- ============================================================================================================
-- POST /api/v1/eligibility/check returns the verdict plus a cost-share preview: tier, terms, and the
-- allowed / member / payer split. No diagnosis, no clinical history, no other member's data. The read is
-- audited as a PHI read against the beneficiary, exactly as reception's and the doctor's already are — so a
-- pharmacist asking about a member who is not in front of them leaves the same trace anyone else would.

INSERT INTO identity.role_scope (role_name, scope_name)
VALUES ('pharmacist', 'eligibility:check')
ON CONFLICT DO NOTHING;

-- Fan out to every provisioned tenant — after 0012 each owns its own grant set and does not inherit live.
INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT DISTINCT rs.tenant_id, 'pharmacist', 'eligibility:check'
FROM identity.role_scope rs
WHERE rs.tenant_id <> ''
ON CONFLICT DO NOTHING;
