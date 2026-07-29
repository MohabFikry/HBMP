-- identity-service — 0008 the note scopes (phase 19.3). Additive + idempotent.
--
-- Notes are a surface of their own, not a corner of policy administration, so they carry their own scopes:
--
--   note:read    read notes on a policy or a member. Deliberately WIDE — nearly everyone who works a case
--                needs to see that notes exist and read the administrative ones. Minimum-necessary bites at
--                the BODY, by visibility_class, in NoteVisibilityRules — not here. Withholding the whole
--                surface from finance or the call centre would make the member record look empty and send
--                them away believing nothing was written.
--   note:write   author a note, cancel your own, pin. Narrower: a note is a signed statement.
--
-- Cancelling ANOTHER user's note additionally requires policy:supervise (design 38 §5.5), which already
-- exists from 19.1 — it is the supervisory increment over member administration, and withdrawing a
-- colleague's signed statement is exactly that.

INSERT INTO identity.scope (name, domain, service_only) VALUES
    ('note:read',  'policy', false),
    ('note:write', 'policy', false)
ON CONFLICT (name) DO NOTHING;

-- Reading: every role that works a member's case. The clinical roles are here because a clinician reading a
-- member's administrative history is ordinary care coordination; what they may read the BODY of is a separate
-- question the projection answers.
INSERT INTO identity.role_scope (role_name, scope_name)
SELECT role, 'note:read' FROM (VALUES
    ('beneficiary_mgmt'), ('medical_approval'), ('finance'), ('claims_officer'), ('call_center'),
    ('doctor'), ('nurse'), ('case_manager'), ('medical_director'), ('reception'),
    ('org_admin'), ('super_admin')
) AS r(role)
-- Bare conflict target: 0011 widened role_scope's PK to (tenant_id, role_name, scope_name), and an
-- ON CONFLICT target is resolved against the constraints that exist when the statement RUNS — so naming
-- the old pair made this file un-re-runnable from 0011 onwards. See 0001 for the full note.
ON CONFLICT DO NOTHING;

-- Authoring: the roles that record decisions about a case. Reception is READ-only here — they see that a note
-- exists but do not author signed statements about a member's benefit.
INSERT INTO identity.role_scope (role_name, scope_name)
SELECT role, 'note:write' FROM (VALUES
    ('beneficiary_mgmt'), ('medical_approval'), ('finance'), ('claims_officer'), ('call_center'),
    ('doctor'), ('nurse'), ('case_manager'), ('medical_director'),
    ('org_admin'), ('super_admin')
) AS r(role)
-- Bare conflict target: 0011 widened role_scope's PK to (tenant_id, role_name, scope_name), and an
-- ON CONFLICT target is resolved against the constraints that exist when the statement RUNS — so naming
-- the old pair made this file un-re-runnable from 0011 onwards. See 0001 for the full note.
ON CONFLICT DO NOTHING;
