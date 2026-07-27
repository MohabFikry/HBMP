-- audit-service — 0002 a dedicated NOBYPASSRLS login role for the runtime connection (audit R2 X6 class).
--
-- 0001 built the immutability story properly: an append-only writer role, a deny-mutation trigger, and
-- FORCE row-level security whose policies are keyed on ROLE MEMBERSHIP (pg_has_role) rather than a tenant
-- GUC. Then compose connected audit-service as the Postgres SUPERUSER, which bypasses RLS entirely — so the
-- p_audit_read / p_audit_insert policies never evaluated on the only connection that uses them. The
-- append-only TRIGGER still fired (triggers are not bypassed), which is why this never showed up as a
-- failure: the loudest control still worked and the quiet one did not.
--
-- audit does NOT move to hbmp_app. hbmp_app is the shared runtime role for every other service; granting it
-- membership in hbmp_audit_writer would let all twenty of them read the audit trail — the exact opposite of
-- what §10 isolation asks for. It gets its own login role with membership in the writer group and nothing
-- else. Additive + idempotent.

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_audit') THEN
        CREATE ROLE hbmp_audit LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

-- Membership in the writer group is what satisfies p_audit_insert / p_audit_read. INSERT + SELECT only —
-- UPDATE/DELETE are neither granted by the group nor survivable past trg_audit_no_update.
GRANT hbmp_audit_writer TO hbmp_audit;
GRANT USAGE ON SCHEMA audit TO hbmp_audit;

-- Non-audit_event tables in the schema (retention config, the outbox) are ordinary service state.
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA audit TO hbmp_audit;
REVOKE UPDATE, DELETE ON audit.audit_event FROM hbmp_audit;
ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT SELECT, INSERT, UPDATE ON TABLES TO hbmp_audit;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA audit TO hbmp_audit;

-- The password is set out of band (OpenBao secret/hbmp/db/audit; HBMP_AUDIT_PASSWORD locally) — never in a
-- tracked migration. Same convention as hbmp_app (patient 0003).
