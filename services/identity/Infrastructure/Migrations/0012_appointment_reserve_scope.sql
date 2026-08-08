-- identity-service — 0012 grant the call centre appointment:reserve. ADDITIVE / idempotent.
--
-- The call centre could not complete a booking. Its façade (callcentre-service, callcentre:act + a verified
-- interaction) forwards the AGENT's token to emr, and emr guarded POST /appointments with appointment:write —
-- the same scope as check-in and no-show. The call centre is deliberately not given those, so every reservation
-- ended in a bare 403 from emr, after passing every call-centre gate. Verified against the running stack.
--
-- emr now guards booking/reschedule/cancel with (appointment:write OR appointment:reserve) and leaves check-in
-- and no-show on appointment:write alone. This grant is the other half: reservation powers WITHOUT arrival
-- powers, which is what "reservation-only, wider scope" actually means.

-- The scope has to exist before anything can be granted it (role_scope_scope_name_fkey).
--
-- FOUR columns, not six. `deprecated` and `is_platform_admin_key` are added by 0013, one migration LATER, so
-- naming them here fails on any database built from scratch: "column deprecated of relation scope does not
-- exist". It ran fine on every long-lived database, where 0013 had been applied by the time anyone re-ran
-- 0012 — so the whole of CI stopped at this line while every developer machine was green. Both columns
-- arrive NOT NULL DEFAULT false, which is exactly what this row was specifying anyway.
INSERT INTO identity.scope (name, domain, description, service_only)
VALUES ('appointment:reserve', 'appointment',
        'Book, reschedule and cancel appointments WITHOUT the arrival decisions (check-in, no-show).',
        false)
ON CONFLICT (name) DO NOTHING;

INSERT INTO identity.role_scope (role_name, scope_name, tenant_id)
SELECT 'call_center', 'appointment:reserve', tenant_id
  FROM identity.role_scope
 WHERE role_name = 'call_center' AND scope_name = 'appointment:read'
ON CONFLICT DO NOTHING;
